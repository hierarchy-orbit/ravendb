﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Nevar.Debugging;
using Nevar.Impl;
using Nevar.Impl.FileHeaders;
using Nevar.Trees;

namespace Nevar
{
	public unsafe class StorageEnvironment : IDisposable
	{
		private readonly ConcurrentDictionary<long, Transaction> _activeTransactions = new ConcurrentDictionary<long, Transaction>();

		private readonly Dictionary<string, Tree> _trees = new Dictionary<string, Tree>();

		private readonly bool _ownsPager;
		private readonly IVirtualPager _pager;
		private readonly SliceComparer _sliceComparer;

		private readonly SemaphoreSlim _txWriter = new SemaphoreSlim(1);

		private long _transactionsCounter;
		private readonly FreeSpaceCollector _freeSpaceCollector;

		public StorageEnvironment(IVirtualPager pager, bool ownsPager = true)
		{
			try
			{
				_pager = pager;
				_ownsPager = ownsPager;
				_freeSpaceCollector = new FreeSpaceCollector(this);
				_sliceComparer = NativeMethods.memcmp;

				Setup(pager);

				FreeSpace.Name = "Free Space";
				Root.Name = "Root";
			}
			catch (Exception)
			{
				if (ownsPager)
					pager.Dispose();
			}
		}

		private void Setup(IVirtualPager pager)
		{
			if (pager.NumberOfAllocatedPages == 0)
			{
				WriteEmptyHeaderPage(_pager.Get(null, 0));
				WriteEmptyHeaderPage(_pager.Get(null, 1));

				NextPageNumber = 2;
				using (var tx = new Transaction(_pager, this, _transactionsCounter + 1, TransactionFlags.ReadWrite))
				{
					var root = Tree.Create(tx, _sliceComparer);
					var freeSpace = Tree.Create(tx, _sliceComparer);

					// important to first create the two trees, then set them on the env

					FreeSpace = freeSpace;
					Root = root;

					tx.UpdateRoots(root, freeSpace);

					tx.Commit();
				}
				return;
			}
			// existing db, let us load it

			// the first two pages are allocated for double buffering tx commits
			FileHeader* entry = FindLatestFileHeadeEntry();
			NextPageNumber = entry->LastPageNumber + 1;
			_transactionsCounter = entry->TransactionId + 1;
			using (var tx = new Transaction(_pager, this, _transactionsCounter + 1, TransactionFlags.ReadWrite))
			{
				var root = Tree.Open(tx, _sliceComparer, &entry->Root);
				var freeSpace = Tree.Open(tx, _sliceComparer, &entry->FreeSpace);

				// important to first create the two trees, then set them on the env
				FreeSpace = freeSpace;
				Root = root;

				tx.Commit();
			}
		}

		public long NextPageNumber { get; set; }

		public SliceComparer SliceComparer
		{
			get { return _sliceComparer; }
		}

		public Tree Root { get; private set; }
		public Tree FreeSpace { get; private set; }

		public long OldestTransaction
		{
			get { return _activeTransactions.Keys.OrderBy(x => x).FirstOrDefault(); }
		}

		public int PageSize
		{
			get { return _pager.PageSize; }
		}

		public Tree GetTree(string name)
		{
			Tree tree;
			if (_trees.TryGetValue(name, out tree))
				return tree;
			throw new InvalidOperationException("No such tree: " + name);
		}

		public Tree CreateTree(Transaction tx, string name)
		{
			if (tx.Flags == (TransactionFlags.ReadWrite) == false)
				throw new ArgumentException("Cannot create a new tree with a read only transaction");

			Tree tree;
			if (_trees.TryGetValue(name, out tree))
				return tree;

			Slice key = name;

			// we are in a write transaction, no need to handle locks
			var header = (TreeRootHeader*)Root.DirectRead(tx, key);
			if (header != null)
			{
				tree = Tree.Open(tx, _sliceComparer, header);
				tree.Name = name;
				_trees.Add(name, tree);
				return tree;
			}

			tree = Tree.Create(tx, _sliceComparer);
			tree.Name = name;
			var space = tree.DirectAdd(tx, key, sizeof(TreeRootHeader));
			tree.State.CopyTo((TreeRootHeader*)space);

			_trees.Add(name, tree);

			return tree;
		}

		public void Dispose()
		{
			if (_ownsPager)
				_pager.Dispose();
		}

		private void WriteEmptyHeaderPage(Page pg)
		{
			var fileHeader = ((FileHeader*)pg.Base);
			fileHeader->MagicMarker = Constants.MagicMarker;
			fileHeader->Version = Constants.CurrentVersion;
			fileHeader->TransactionId = 0;
			fileHeader->LastPageNumber = 1;
			fileHeader->FreeSpace.RootPageNumber = -1;
			fileHeader->Root.RootPageNumber = -1;
		}

		private FileHeader* FindLatestFileHeadeEntry()
		{
			Page fst = _pager.Get(null, 0);
			Page snd = _pager.Get(null, 1);

			FileHeader* e1 = GetFileHeaderFrom(fst);
			FileHeader* e2 = GetFileHeaderFrom(snd);

			FileHeader* entry = e1;
			if (e2->TransactionId > e1->TransactionId)
			{
				entry = e2;
			}
			return entry;
		}

		private FileHeader* GetFileHeaderFrom(Page p)
		{
			var fileHeader = ((FileHeader*)p.Base);
			if (fileHeader->MagicMarker != Constants.MagicMarker)
				throw new InvalidDataException(
					"The header page did not start with the magic marker, probably not a db file");
			if (fileHeader->Version != Constants.CurrentVersion)
				throw new InvalidDataException("This is a db file for version " + fileHeader->Version +
											   ", which is not compatible with the current version " +
											   Constants.CurrentVersion);
			if (fileHeader->LastPageNumber >= _pager.NumberOfAllocatedPages)
				throw new InvalidDataException("The last page number is beyond the number of allocated pages");
			if (fileHeader->TransactionId < 0)
				throw new InvalidDataException("The transaction number cannot be negative");
			return fileHeader;
		}

		public Transaction NewTransaction(TransactionFlags flags)
		{
			bool txLockTaken = false;
			try
			{
				long txId = _transactionsCounter;
				if (flags == (TransactionFlags.ReadWrite))
				{
					txId = _transactionsCounter + 1;
					_txWriter.Wait();
					txLockTaken = true;
				}
				var tx = new Transaction(_pager, this, txId, flags);
				_activeTransactions.TryAdd(txId, tx);
				var state = _pager.TransactionBegan();
				tx.AddPagerState(state);


				if (flags == (TransactionFlags.ReadWrite))
					tx.SetFreeSpaceCollector(_freeSpaceCollector);

				return tx;
			}
			catch (Exception)
			{
				if (txLockTaken)
					_txWriter.Release();
				throw;
			}
		}

		internal void TransactionCompleted(long txId)
		{
			Transaction tx;
			if (_activeTransactions.TryRemove(txId, out tx) == false)
				return;

			if (tx.Flags != (TransactionFlags.ReadWrite))
				return;

			_transactionsCounter = txId;
			_txWriter.Release();
		}

		public EnvironmentStats Stats()
		{
			var results = new EnvironmentStats
				{
					FreePagesOverhead = FreeSpace.State.PageCount,
					RootPages = Root.State.PageCount,
					HeaderPages = 2,
					UnallocatedPagesAtEndOfFile = _pager.NumberOfAllocatedPages - NextPageNumber
				};
			using (Transaction tx = NewTransaction(TransactionFlags.Read))
			{
				using (Iterator it = FreeSpace.Iterate(tx))
				{
					var slice = new Slice(SliceOptions.Key);
					if (it.Seek(Slice.BeforeAllKeys))
					{
						do
						{
							slice.Set(it.Current);

							var ft = new EnvironmentStats.FreedTransaction
								{
									Id = slice.ToInt64()
								};

							results.FreedTransactions.Add(ft);

							int numberOfFreePages = tx.GetNumberOfFreePages(it.Current);
							results.FreePages += numberOfFreePages;
							using (Stream data = Tree.StreamForNode(tx, it.Current))
							using (var reader = new BinaryReader(data))
							{
								for (int i = 0; i < numberOfFreePages; i++)
								{
									ft.Pages.Add(reader.ReadInt64());
								}
							}
						} while (it.MoveNext());
					}
				}
				tx.Commit();
			}

			return results;
		}
	}
}