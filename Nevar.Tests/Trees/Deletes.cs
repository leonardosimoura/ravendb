﻿using System;
using System.Collections.Generic;
using System.IO;
using Nevar.Impl;
using Nevar.Trees;
using Xunit;

namespace Nevar.Tests.Trees
{
	public class Deletes : StorageTest
	{

		[Fact]
		public void CanAddVeryLargeValueAndThenDeleteIt()
		{
			var random = new Random();
			var buffer = new byte[8192];
			random.NextBytes(buffer);

            using (var tx = Env.NewTransaction(TransactionFlags.ReadWrite))
			{
				Env.Root.Add(tx, "a", new MemoryStream(buffer));

				tx.Commit();
			}

			Assert.Equal(4, Env.Root.State.PageCount);
			Assert.Equal(3, Env.Root.State.OverflowPages);
                                    
            using (var tx = Env.NewTransaction(TransactionFlags.ReadWrite))
			{
				Env.Root.Delete(tx, "a");

				tx.Commit();
			}


			Assert.Equal(1, Env.Root.State.PageCount);
			Assert.Equal(0, Env.Root.State.OverflowPages);

            using (var tx = Env.NewTransaction(TransactionFlags.Read))
			{
				Assert.Null(Env.Root.Read(tx, "a"));

				tx.Commit();
			}

			
		}

		 [Fact]
		 public void CanDeleteAtRoot()
		 {
             using (var tx = Env.NewTransaction(TransactionFlags.ReadWrite))
			 {
				 for (int i = 0; i < 1000; i++)
				 {
					 Env.Root.Add(tx, string.Format("{0,5}",i), StreamFor("abcdefg"));
				 }
				 tx.Commit();
			 }

             var expected = new List<Slice>();
             for (int i = 15; i < 1000; i++)
             {
                 expected.Add(string.Format("{0,5}", i));
             }

             using (var tx = Env.NewTransaction(TransactionFlags.ReadWrite))
			 {
				 for (int i = 0; i < 15; i++)
				 {
					 Env.Root.Delete(tx, string.Format("{0,5}", i));
				 }
				 tx.Commit();
			 }


             using (var tx = Env.NewTransaction(TransactionFlags.Read))
			 {
                 var list = Keys(Env.Root, tx);
				 Assert.Equal(expected, list);
			 }
		 }

        public unsafe List<Slice> Keys(Tree t, Transaction tx)
        {
            var results = new List<Slice>();
            using (var it = t.Iterate(tx))
            {
                if (it.Seek(Slice.BeforeAllKeys) == false)
                    return results;
                do
                {
                    results.Add(new Slice(it.Current));
                } while (it.MoveNext());
            }
            return results;
        }
	}
}