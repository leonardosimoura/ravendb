﻿using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client.Documents.Operations;
using Xunit;
using Raven.Client;
using Raven.Server.Documents;
using Raven.Server.ServerWide.Context;

namespace FastTests.Client.Attachments
{
    public class AttachmentsCrud : RavenTestBase
    {
        [Fact]
        public void PutAttachments()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenSession())
                {
                    session.Store(new User { Name = "Fitzchak" }, "users/1");
                    session.SaveChanges();
                }

                var names = new[]
                {
                    "profile.png",
                    "background-photo.jpg",
                    "fileNAME_#$1^%_בעברית.txt"
                };
                using (var profileStream = new MemoryStream(new byte[] {1, 2, 3}))
                {
                    var result = store.Operations.Send(new PutAttachmentOperation("users/1", names[0], profileStream, "image/png"));
                    Assert.Equal(2, result.Etag);
                    Assert.Equal(names[0], result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal("image/png", result.ContentType);
                    Assert.Equal("A5BYxvLAy0ksUzsKTRTvd8wPeKvMztUofYShogEc+4E=", result.Hash);
                }
                using (var backgroundStream = new MemoryStream(new byte[] {10, 20, 30, 40, 50}))
                {
                    var result = store.Operations.Send(new PutAttachmentOperation("users/1", names[1], backgroundStream, "ImGgE/jPeG"));
                    Assert.Equal(4, result.Etag);
                    Assert.Equal(names[1], result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal("ImGgE/jPeG", result.ContentType);
                    Assert.Equal("bvU6ZAjsRCl5H6gI5PvnhD+TYxaVCiC6X7u90BzSmJk=", result.Hash);
                }
                using (var fileStream = new MemoryStream(new byte[] {1, 2, 3, 4, 5}))
                {
                    var result = store.Operations.Send(new PutAttachmentOperation("users/1", names[2], fileStream, null));
                    Assert.Equal(6, result.Etag);
                    Assert.Equal(names[2], result.Name);
                    Assert.Equal("users/1", result.DocumentId);
                    Assert.Equal("", result.ContentType);
                    Assert.Equal("dPgf4WfZm0y0HW0MzagieMrunz4vJdXlo5Nv89zsYNA=", result.Hash);
                }

                using (var session = store.OpenSession())
                {
                    var user = session.Load<User>("users/1");
                    var metadata = session.Advanced.GetMetadataFor(user);
                    Assert.Equal(DocumentFlags.HasAttachments.ToString(), metadata[Constants.Documents.Metadata.Flags]);
                    var attachments = metadata.GetObjects(Constants.Documents.Metadata.Attachments);
                    Assert.Equal(3, attachments.Length);
                    var orderedNames = names.OrderBy(x => x).ToArray();
                    for (var i = 0; i < names.Length; i++)
                    {
                        var name = orderedNames[i];
                        var attachment = attachments[i];
                        Assert.Equal(name, attachment.GetString(nameof(Attachment.Name)));
                        Assert.Contains("=", attachment.GetString(nameof(Attachment.Hash)));
                    }
                }

                var statistics = store.Admin.Send(new GetStatisticsOperation());
                Assert.Equal(3, statistics.CountOfAttachments);
                Assert.Equal(1, statistics.CountOfDocuments);
                Assert.Equal(0, statistics.CountOfIndexes);

                var readBuffer = new byte[8];
                for (var i = 0; i < names.Length; i++)
                {
                    var name = names[i];
                    using (var attachmentStream = new MemoryStream(readBuffer))
                    {
                        var attachment = store.Operations.Send(new GetAttachmentOperation("users/1", name, (result,stream) => stream.CopyTo(attachmentStream)));
                        Assert.Equal(2 + 2 * i, attachment.Etag);
                        Assert.Equal(name, attachment.Name);
                        Assert.Equal(i == 0 ? 3 : 5, attachmentStream.Position);
                        if (i == 0)
                        {
                            Assert.Equal(new byte[] {1, 2, 3}, readBuffer.Take(3));
                            Assert.Equal("image/png", attachment.ContentType);
                            Assert.Equal("A5BYxvLAy0ksUzsKTRTvd8wPeKvMztUofYShogEc+4E=", attachment.Hash);
                        }
                        else if (i == 1)
                        {
                            Assert.Equal(new byte[] {10, 20, 30, 40, 50}, readBuffer.Take(5));
                            Assert.Equal("ImGgE/jPeG", attachment.ContentType);
                            Assert.Equal("bvU6ZAjsRCl5H6gI5PvnhD+TYxaVCiC6X7u90BzSmJk=", attachment.Hash);
                        }
                        else if (i == 2)
                        {
                            Assert.Equal(new byte[] {1, 2, 3, 4, 5}, readBuffer.Take(5));
                            Assert.Null(attachment.ContentType);
                            Assert.Equal("dPgf4WfZm0y0HW0MzagieMrunz4vJdXlo5Nv89zsYNA=", attachment.Hash);
                        }
                    }
                }
                using (var attachmentStream = new MemoryStream(readBuffer))
                {
                    var notExistsAttachment = store.Operations.Send(new GetAttachmentOperation("users/1", "not-there", (result, stream) => stream.CopyTo(attachmentStream)));
                    Assert.Null(notExistsAttachment);
                }
            }
        }

        [Fact]
        public async Task DeleteAttachments()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenSession())
                {
                    session.Store(new User {Name = "Fitzchak"}, "users/1");
                    session.SaveChanges();
                }

                for (int i = 1; i <= 3; i++)
                {
                    using (var profileStream = new MemoryStream(Enumerable.Range(1, 3 * i).Select(x => (byte) x).ToArray()))
                        store.Operations.Send(new PutAttachmentOperation("users/1", "file" + i, profileStream, "image/png"));
                }
                Assert.Equal(3, store.Admin.Send(new GetStatisticsOperation()).CountOfAttachments);

                store.Operations.Send(new DeleteAttachmentOperation("users/1", "file2"));
                Assert.Equal(2, store.Admin.Send(new GetStatisticsOperation()).CountOfAttachments);

                using (var session = store.OpenSession())
                {
                    var user = session.Load<User>("users/1");
                    var metadata = session.Advanced.GetMetadataFor(user);
                    Assert.Equal(DocumentFlags.HasAttachments.ToString(), metadata[Constants.Documents.Metadata.Flags]);
                    Assert.Equal("file1,file3", metadata[Constants.Documents.Metadata.Attachments]);
                }

                var readBuffer = new byte[16];
                using (var attachmentStream = new MemoryStream(readBuffer))
                {
                    var attachment = store.Operations.Send(new GetAttachmentOperation("users/1", "file1", (result, stream) => stream.CopyTo(attachmentStream)));
                    Assert.Equal(2, attachment.Etag);
                    Assert.Equal("file1", attachment.Name);
                    Assert.Equal(3, attachmentStream.Position);
                    Assert.Equal(new byte[] {1, 2, 3}, readBuffer.Take(3));
                }
                using (var attachmentStream = new MemoryStream(readBuffer))
                {
                    var attachment = store.Operations.Send(new GetAttachmentOperation("users/1", "file3", (result, stream) => stream.CopyTo(attachmentStream)));
                    Assert.Equal(6, attachment.Etag);
                    Assert.Equal("file3", attachment.Name);
                    Assert.Equal(9, attachmentStream.Position);
                    Assert.Equal(new byte[] {1, 2, 3, 4, 5, 6, 7, 8, 9}, readBuffer.Take(9));
                }

                // Delete document should delete all the attachments
                store.Commands().Delete("users/1", null);
                var database = await GetDocumentDatabaseInstanceFor(store);
                using (var context = DocumentsOperationContext.ShortTermSingleUse(database))
                using (context.OpenReadTransaction())
                {
                    database.DocumentsStorage.AssertNoAttachmentsForDocument(context, "users/1");
                }

                Assert.Equal(0, store.Admin.Send(new GetStatisticsOperation()).CountOfAttachments);
            }
        }

        private class User
        {
            public string Name { get; set; }
        }
    }
}