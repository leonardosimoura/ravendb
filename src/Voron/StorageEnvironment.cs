﻿using Sparrow;
using Sparrow.Collections;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Sparrow.Logging;
using Voron.Data;
using Voron.Data.BTrees;
using Voron.Data.Fixed;
using Voron.Debugging;
using Voron.Exceptions;
using Voron.Global;
using Voron.Impl;
using Voron.Impl.FileHeaders;
using Voron.Impl.FreeSpace;
using Voron.Impl.Journal;
using Voron.Impl.Paging;
using Voron.Impl.Scratch;
using Voron.Platform.Win32;
using Voron.Util;
using Voron.Util.Conversion;

namespace Voron
{
    public class StorageEnvironment : IDisposable
    {
        public void QueueForSyncDataFile()
        {
            GlobalFlushingBehavior.GlobalFlusher.Value.MaybeSyncEnvironment(this);
        }

        public void ForceSyncDataFile()
        {
            GlobalFlushingBehavior.GlobalFlusher.Value.ForceFlushAndSyncEnvironment(this);
        }

        /// <summary>
        /// This is the shared storage where we are going to store all the static constants for names.
        /// WARNING: This context will never be released, so only static constants should be added here.
        /// </summary>
        public static readonly ByteStringContext LabelsContext = new ByteStringContext(ByteStringContext.MinBlockSizeInBytes);

        /// <summary>
        /// This value is here to control fairness in the access to the write transaction lock,
        /// when we are flushing to the data file, there is a need to take the write tx lock to mutate
        /// some of our in memory data structures safely. However, if there is a thread, such as tx merger thread,
        /// that is doing high frequency transactions, the unfairness in the lock will automatically ensure that we'll
        /// have very hard time actually getting the write lock in the flushing thread.
        ///
        /// When this value is set to a non negative number, we check if the current thread id equals to this number, and if not
        /// we'll yield the thread for 1 ms. The idea is that this will allow the flushing thread, which doesn't have this limitation
        /// to actually capture the lock, do its work, and then reset it.
        /// </summary>
        private int _otherThreadsShouldWaitBeforeGettingWriteTxLock = -1;

        /// <summary>
        /// Accessing the Thread.CurrentThread.ManagedThreadId can be expensive,
        /// so we cache it.
        /// </summary>
        [ThreadStatic]
        private static int _localThreadIdCopy;

        private readonly StorageEnvironmentOptions _options;

        public readonly ActiveTransactions ActiveTransactions = new ActiveTransactions();

        private readonly AbstractPager _dataPager;
        internal ExceptionDispatchInfo FlushingTaskFailure;
        private readonly WriteAheadJournal _journal;
        private readonly object _txWriter = new object();
        internal readonly ReaderWriterLockSlim FlushInProgressLock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
        private readonly ReaderWriterLockSlim _txCommit = new ReaderWriterLockSlim();

        private long _transactionsCounter;
        private readonly IFreeSpaceHandling _freeSpaceHandling;
        private readonly HeaderAccessor _headerAccessor;

        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly ScratchBufferPool _scratchBufferPool;
        private EndOfDiskSpaceEvent _endOfDiskSpace;
        internal int SizeOfUnflushedTransactionsInJournalFile;

        private readonly Queue<TemporaryPage> _tempPagesPool = new Queue<TemporaryPage>();
        public bool Disposed;
        private readonly Logger _log;
        public static int MaxConcurrentFlushes = 10; // RavenDB-5221

        public Guid DbId { get; set; }

        public StorageEnvironmentState State { get; private set; }


        public StorageEnvironment(StorageEnvironmentOptions options)
        {
            try
            {
                _log = LoggingSource.Instance.GetLogger<StorageEnvironment>(options.BasePath);
                _options = options;
                _dataPager = options.DataPager;
                _freeSpaceHandling = new FreeSpaceHandling();
                _headerAccessor = new HeaderAccessor(this);
                var isNew = _headerAccessor.Initialize();

                _scratchBufferPool = new ScratchBufferPool(this);

                _journal = new WriteAheadJournal(this);

                if (isNew)
                    CreateNewDatabase();
                else // existing db, let us load it
                    LoadExistingDatabase();

                if (_options.ManualFlushing == false)
                    Task.Run(IdleFlushTimer);
            }
            catch (Exception)
            {
                Dispose();
                throw;
            }
        }

        private async Task IdleFlushTimer()
        {
            var cancellationToken = _cancellationTokenSource.Token;

            while (cancellationToken.IsCancellationRequested == false)
            {
                if (Disposed)
                    return;

                if (Options.ManualFlushing)
                    return;

                try
                {
                    await Task.Delay(Options.IdleFlushTimeout, cancellationToken);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                if (Volatile.Read(ref SizeOfUnflushedTransactionsInJournalFile) != 0)
                    GlobalFlushingBehavior.GlobalFlusher.Value.MaybeFlushEnvironment(this);
            }
        }

        public ScratchBufferPool ScratchBufferPool => _scratchBufferPool;

        private unsafe void LoadExistingDatabase()
        {
            var header = stackalloc TransactionHeader[1];
            bool hadIntegrityIssues = _journal.RecoverDatabase(header);

            if (hadIntegrityIssues)
            {
                var message = _journal.Files.Count == 0 ? "Unrecoverable database" : "Database recovered partially. Some data was lost.";

                _options.InvokeRecoveryError(this, message, null);
            }

            var entry = _headerAccessor.CopyHeader();
            var nextPageNumber = (header->TransactionId == 0 ? entry.LastPageNumber : header->LastPageNumber) + 1;
            State = new StorageEnvironmentState(null, nextPageNumber)
            {
                NextPageNumber = nextPageNumber,
                Options = Options
            };

            _transactionsCounter = (header->TransactionId == 0 ? entry.TransactionId : header->TransactionId);

            using (var tx = NewLowLevelTransaction(new TransactionPersistentContext(true), TransactionFlags.ReadWrite))
            {
                var root = Tree.Open(tx, null, header->TransactionId == 0 ? &entry.Root : &header->Root);
                root.Name = Constants.RootTreeNameSlice;

                tx.UpdateRootsIfNeeded(root);

                var treesTx = new Transaction(tx);

                var metadataTree = treesTx.ReadTree(Constants.MetadataTreeNameSlice);
                if (metadataTree == null)
                    throw new VoronUnrecoverableErrorException("Could not find metadata tree in database, possible mismatch / corruption?");

                var dbId = metadataTree.Read("db-id");
                if (dbId == null)
                    throw new VoronUnrecoverableErrorException("Could not find db id in metadata tree, possible mismatch / corruption?");

                var buffer = new byte[16];
                var dbIdBytes = dbId.Reader.Read(buffer, 0, 16);
                if (dbIdBytes != 16)
                    throw new VoronUnrecoverableErrorException("The db id value in metadata tree wasn't 16 bytes in size, possible mismatch / corruption?");

                DbId = new Guid(buffer);

                var schemaVersion = metadataTree.Read("schema-version");
                if (schemaVersion == null)
                    throw new VoronUnrecoverableErrorException("Could not find schema version in metadata tree, possible mismatch / corruption?");

                var schemaVersionVal = schemaVersion.Reader.ReadLittleEndianInt32();
                if (Options.SchemaVersion != 0 &&
                    schemaVersionVal != Options.SchemaVersion)
                {
                    throw new VoronUnrecoverableErrorException("The schema version of this database is expected to be " +
                                                       Options.SchemaVersion + " but is actually " + schemaVersionVal +
                                                       ". You need to upgrade the schema.");
                }

                tx.Commit();

            }
        }

        private void CreateNewDatabase()
        {
            const int initialNextPageNumber = 0;
            State = new StorageEnvironmentState(null, initialNextPageNumber)
            {
                Options = Options
            };
            using (var tx = NewLowLevelTransaction(new TransactionPersistentContext(), TransactionFlags.ReadWrite))
            {
                var root = Tree.Create(tx, null);

                // important to first create the root trees, then set them on the env
                tx.UpdateRootsIfNeeded(root);

                var treesTx = new Transaction(tx);

                DbId = Guid.NewGuid();

                var metadataTree = treesTx.CreateTree(Constants.MetadataTreeNameSlice);
                metadataTree.Add("db-id", DbId.ToByteArray());
                metadataTree.Add("schema-version", EndianBitConverter.Little.GetBytes(Options.SchemaVersion));

                treesTx.PrepareForCommit();

                tx.Commit();
            }

        }

        public IFreeSpaceHandling FreeSpaceHandling => _freeSpaceHandling;

        public HeaderAccessor HeaderAccessor => _headerAccessor;

        public long NextPageNumber => State.NextPageNumber;

        public StorageEnvironmentOptions Options => _options;

        public WriteAheadJournal Journal => _journal;

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
            try
            {
                if (_journal != null) // error during ctor
                {
                    // if there is a pending flush operation, we need to wait for it
                    bool lockTaken = false;
                    using (_journal.Applicator.TryTakeFlushingLock(ref lockTaken))
                    {
                        // note that we have to set the dispose flag when under the lock
                        // (either with TryTake or Take), to avoid data race between the
                        // flusher & the dispose. The flusher will check the dispose status
                        // only after it successfully took the lock.

                        if (lockTaken == false)
                        {
                            // if we are here, then we didn't get the flush lock, so it is currently being run
                            // we need to wait for it to complete (so we won't be shutting down the db while we
                            // are flushing and maybe access invalid memory.
                            using (_journal.Applicator.TakeFlushingLock())
                            {
                                // when we are here, we know that we aren't flushing, and we can dispose,
                                // any future calls to flush will abort because we are marked as disposed

                                Disposed = true;
                                _journal.Applicator.WaitForSyncToCompleteOnDispose();
                            }
                        }
                        else
                        {
                            Disposed = true;
                            _journal.Applicator.WaitForSyncToCompleteOnDispose();
                        }
                    }
                }
            }
            finally
            {
                var errors = new List<Exception>();
                foreach (var disposable in new IDisposable[]
                {
                    _headerAccessor,
                    _scratchBufferPool,
                    _journal,
                    _options.OwnsPagers ? _options : null
                }.Concat(_tempPagesPool))
                {
                    try
                    {
                        disposable?.Dispose();
                    }
                    catch (Exception e)
                    {
                        errors.Add(e);
                    }
                }

                if (errors.Count != 0)
                    throw new AggregateException(errors);
            }
        }

        public Transaction ReadTransaction(TransactionPersistentContext transactionPersistentContext, ByteStringContext context = null)
        {
            return new Transaction(NewLowLevelTransaction(transactionPersistentContext, TransactionFlags.Read, context));
        }

        public Transaction ReadTransaction(ByteStringContext context = null)
        {
            return new Transaction(NewLowLevelTransaction(new TransactionPersistentContext(), TransactionFlags.Read, context));
        }

        public Transaction WriteTransaction(TransactionPersistentContext transactionPersistentContext, ByteStringContext context = null)
        {
            return new Transaction(NewLowLevelTransaction(transactionPersistentContext, TransactionFlags.ReadWrite, context, null));
        }

        public Transaction WriteTransaction(ByteStringContext context = null)
        {
            return new Transaction(NewLowLevelTransaction(new TransactionPersistentContext(), TransactionFlags.ReadWrite, context, null));
        }

        internal LowLevelTransaction NewLowLevelTransaction(TransactionPersistentContext transactionPersistentContext, TransactionFlags flags, ByteStringContext context = null, TimeSpan? timeout = null)
        {
            bool txLockTaken = false;
            bool flushInProgressReadLockTaken = false;
            try
            {
                if (flags == TransactionFlags.ReadWrite)
                {
                    var wait = timeout ?? (Debugger.IsAttached ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(30));

                    GiveFlusherChanceToRunIfNeeded();

                    if (FlushInProgressLock.IsWriteLockHeld == false)
                        flushInProgressReadLockTaken = FlushInProgressLock.TryEnterReadLock(wait);
                    Monitor.TryEnter(_txWriter, wait, ref txLockTaken);
                    if (txLockTaken == false || (flushInProgressReadLockTaken == false && FlushInProgressLock.IsWriteLockHeld == false))
                    {
                        GlobalFlushingBehavior.GlobalFlusher.Value.MaybeFlushEnvironment(this);
                        throw new TimeoutException("Waited for " + wait +
                                                    " for transaction write lock, but could not get it");
                    }
                    if (_endOfDiskSpace != null)
                    {
                        if (_endOfDiskSpace.CanContinueWriting)
                        {
                            FlushingTaskFailure = null;
                            _endOfDiskSpace = null;
                            _cancellationTokenSource = new CancellationTokenSource();
                            Task.Run(IdleFlushTimer);
                            GlobalFlushingBehavior.GlobalFlusher.Value.MaybeFlushEnvironment(this);
                        }
                    }
                }

                LowLevelTransaction tx;

                _txCommit.EnterReadLock();
                try
                {
                    long txId = flags == TransactionFlags.ReadWrite ? _transactionsCounter + 1 : _transactionsCounter;
                    tx = new LowLevelTransaction(this, txId, transactionPersistentContext, flags, _freeSpaceHandling, context);
                }
                finally
                {
                    _txCommit.ExitReadLock();
                }

                ActiveTransactions.Add(tx);
                var state = _dataPager.PagerState;
                tx.EnsurePagerStateReference(state);

                return tx;
            }
            catch (Exception)
            {
                if (txLockTaken)
                {
                    Monitor.Exit(_txWriter);
                }
                if (flushInProgressReadLockTaken)
                {
                    FlushInProgressLock.ExitReadLock();
                }
                throw;
            }
        }

        private void GiveFlusherChanceToRunIfNeeded()
        {
            // See comment on the _otherThreadsShouldWaitBeforeGettingWriteTxLock variable for the details
            var localCopy = Volatile.Read(ref _otherThreadsShouldWaitBeforeGettingWriteTxLock);
            if (localCopy == -1)
                return;
            if (_localThreadIdCopy == 0)
            {
                _localThreadIdCopy = Thread.CurrentThread.ManagedThreadId;
            }
            if (_localThreadIdCopy != localCopy)
                Thread.Sleep(1);
        }


        public long NextWriteTransactionId => Volatile.Read(ref _transactionsCounter) + 1;

        public bool IsDataFileEnqueuedToSync { get; set; }

        internal void TransactionAfterCommit(LowLevelTransaction tx)
        {
            if (ActiveTransactions.Contains(tx) == false)
                return;

            _txCommit.EnterWriteLock();
            try
            {
                if (tx.Committed && tx.FlushedToJournal)
                    _transactionsCounter = tx.Id;

                State = tx.State;
            }
            finally
            {
                _txCommit.ExitWriteLock();
            }

            if (tx.FlushedToJournal == false)
                return;

            var totalPages = 0;
            // ReSharper disable once LoopCanBeConvertedToQuery
            foreach (var page in tx.GetTransactionPages())
            {
                totalPages += page.NumberOfPages;
            }

            Interlocked.Add(ref SizeOfUnflushedTransactionsInJournalFile, totalPages);
            if (tx.IsLazyTransaction == false)
                GlobalFlushingBehavior.GlobalFlusher.Value.MaybeFlushEnvironment(this);
        }

        internal void TransactionCompleted(LowLevelTransaction tx)
        {
            if (ActiveTransactions.TryRemove(tx) == false)
                return;

            if (tx.Flags != (TransactionFlags.ReadWrite))
                return;

            Monitor.Exit(_txWriter);
            if (FlushInProgressLock.IsReadLockHeld)
                FlushInProgressLock.ExitReadLock();
        }

        public StorageReport GenerateReport(Transaction tx, bool computeExactSizes = false)
        {
            var numberOfAllocatedPages = Math.Max(_dataPager.NumberOfAllocatedPages, NextPageNumber - 1); // async apply to data file task
            var numberOfFreePages = _freeSpaceHandling.AllPages(tx.LowLevelTransaction).Count;

            var trees = new List<Tree>();
            var fixedSizeTrees = new List<FixedSizeTree>();
            using (var rootIterator = tx.LowLevelTransaction.RootObjects.Iterate(false))
            {
                if (rootIterator.Seek(Slices.BeforeAllKeys))
                {
                    do
                    {
                        var currentKey = rootIterator.CurrentKey.Clone(tx.Allocator);
                        var type = tx.GetRootObjectType(currentKey);
                        switch (type)
                        {
                            case RootObjectType.VariableSizeTree:
                                var tree = tx.ReadTree(currentKey.ToString());
                                trees.Add(tree);
                                break;
                            case RootObjectType.EmbeddedFixedSizeTree:
                                break;
                            case RootObjectType.FixedSizeTree:
                                fixedSizeTrees.Add(tx.FixedTreeFor(currentKey, 0));
                                break;
                            default:
                                throw new ArgumentOutOfRangeException();
                        }
                    }
                    while (rootIterator.MoveNext());
                }
            }

            var generator = new StorageReportGenerator(tx.LowLevelTransaction);

            return generator.Generate(new ReportInput
            {
                NumberOfAllocatedPages = numberOfAllocatedPages,
                NumberOfFreePages = numberOfFreePages,
                NextPageNumber = NextPageNumber,
                Journals = Journal.Files.ToList(),
                Trees = trees,
                FixedSizeTrees = fixedSizeTrees,
                IsLightReport = !computeExactSizes
            });
        }

        public EnvironmentStats Stats()
        {
            using (var tx = NewLowLevelTransaction(new TransactionPersistentContext(), TransactionFlags.Read))
            {
                var numberOfAllocatedPages = Math.Max(_dataPager.NumberOfAllocatedPages, State.NextPageNumber - 1); // async apply to data file task

                return new EnvironmentStats
                {
                    FreePagesOverhead = FreeSpaceHandling.GetFreePagesOverhead(tx),
                    RootPages = tx.RootObjects.State.PageCount,
                    UnallocatedPagesAtEndOfFile = _dataPager.NumberOfAllocatedPages - NextPageNumber,
                    UsedDataFileSizeInBytes = (State.NextPageNumber - 1) * Options.PageSize,
                    AllocatedDataFileSizeInBytes = numberOfAllocatedPages * Options.PageSize,
                    NextWriteTransactionId = NextWriteTransactionId,
                    ActiveTransactions = ActiveTransactions.AllTransactions
                };
            }
        }

        internal void BackgroundFlushWritesToDataFile()
        {
            try
            {
                _journal.Applicator.ApplyLogsToDataFile(ActiveTransactions.OldestTransaction, _cancellationTokenSource.Token,
                    // we intentionally don't wait, if the flush lock is held, something else is flushing, so we don't need
                    // to hold the thread
                    TimeSpan.Zero);
            }
            catch (TimeoutException)
            {
                // we can ignore this, we'll try next time
            }
            catch (SEHException sehException)
            {
                throw new VoronUnrecoverableErrorException("Error occurred during flushing journals to the data file",
                    new Win32Exception(sehException.HResult));
            }
            catch (Exception e)
            {
                throw new VoronUnrecoverableErrorException("Error occurred during flushing journals to the data file",
                    e);
            }
        }

        public void FlushLogToDataFile(LowLevelTransaction tx = null)
        {
            if (_options.ManualFlushing == false)
                throw new NotSupportedException("Manual flushes are not set in the storage options, cannot manually flush!");

            ForceLogFlushToDataFile(tx);
        }

        public void ForceLogFlushToDataFile(LowLevelTransaction tx)
        {
            _journal.Applicator.ApplyLogsToDataFile(ActiveTransactions.OldestTransaction, _cancellationTokenSource.Token,
                Debugger.IsAttached ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(30),
                tx);
        }

        internal void AssertFlushingNotFailed()
        {
            FlushingTaskFailure?.Throw(); // force re-throw of error
        }

        internal void HandleDataDiskFullException(DiskFullException exception)
        {
            if (_options.ManualFlushing)
                return;

            _cancellationTokenSource.Cancel();
            _endOfDiskSpace = new EndOfDiskSpaceEvent(exception.DriveInfo);
        }

        public IDisposable GetTemporaryPage(LowLevelTransaction tx, out TemporaryPage tmp)
        {
            if (tx.Flags != TransactionFlags.ReadWrite)
                throw new ArgumentException("Temporary pages are only available for write transactions");
            if (_tempPagesPool.Count > 0)
            {
                tmp = _tempPagesPool.Dequeue();
                return tmp.ReturnTemporaryPageToPool;
            }

            tmp = new TemporaryPage(Options);
            try
            {
                return tmp.ReturnTemporaryPageToPool = new ReturnTemporaryPageToPool(this, tmp);
            }
            catch (Exception)
            {
                tmp.Dispose();
                throw;
            }
        }

        private class ReturnTemporaryPageToPool : IDisposable
        {
            private readonly TemporaryPage _tmp;
            private readonly StorageEnvironment _env;

            public ReturnTemporaryPageToPool(StorageEnvironment env, TemporaryPage tmp)
            {
                _tmp = tmp;
                _env = env;
            }

            public void Dispose()
            {
                try
                {
                    _env._tempPagesPool.Enqueue(_tmp);
                }
                catch (Exception)
                {
                    _tmp.Dispose();
                    throw;
                }
            }
        }

        public TransactionsModeResult SetTransactionMode(TransactionsMode mode, TimeSpan duration)
        {
            using (var tx = NewLowLevelTransaction(new TransactionPersistentContext(), TransactionFlags.ReadWrite))
            {
                var oldMode = Options.TransactionsMode;

                if (_log.IsOperationsEnabled)
                    _log.Operations($"Setting transaction mode to {mode}. Old mode is {oldMode}");

                if (oldMode == mode)
                    return TransactionsModeResult.ModeAlreadySet;

                Options.TransactionsMode = mode;
                if (duration == TimeSpan.FromMinutes(0)) // infinte
                    Options.NonSafeTransactionExpiration = null;
                else
                    Options.NonSafeTransactionExpiration = DateTime.Now + duration;

                if (oldMode == TransactionsMode.Lazy)
                {

                    tx.IsLazyTransaction = false;
                    // we only commit here, the rest of the of the options are without
                    // commit and we use the tx lock
                    tx.Commit();
                }

                if (oldMode == TransactionsMode.Danger)
                    Journal.TruncateJournal(Options.PageSize);

                switch (mode)
                {
                    case TransactionsMode.Safe:
                    case TransactionsMode.Lazy:
                        {
                            Options.PosixOpenFlags = StorageEnvironmentOptions.SafePosixOpenFlags;
                            Options.WinOpenFlags = StorageEnvironmentOptions.SafeWin32OpenFlags;
                        }
                        break;

                    case TransactionsMode.Danger:
                        {
                            Options.PosixOpenFlags = 0;
                            Options.WinOpenFlags = Win32NativeFileAttributes.None;
                            Journal.TruncateJournal(Options.PageSize);
                        }
                        break;
                    default:
                        {
                            throw new InvalidOperationException("Query string value 'mode' is not a valid mode: " + mode);
                        }
                }

                return TransactionsModeResult.SetModeSuccessfully;
            }
        }

        internal void IncreaseTheChanceForGettingTheTransactionLock()
        {
            Volatile.Write(ref _otherThreadsShouldWaitBeforeGettingWriteTxLock, Thread.CurrentThread.ManagedThreadId);
        }

        internal void ResetTheChanceForGettingTheTransactionLock()
        {
            Volatile.Write(ref _otherThreadsShouldWaitBeforeGettingWriteTxLock, -1);
        }

        public override string ToString()
        {
            return Options.ToString();
        }
    }
}
