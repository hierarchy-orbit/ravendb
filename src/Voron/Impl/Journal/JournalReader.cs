﻿using Sparrow;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Sparrow.Compression;
using Sparrow.Utils;
using Voron.Global;
using Voron.Impl.Paging;

namespace Voron.Impl.Journal
{
    public unsafe class JournalReader : IPagerLevelTransactionState
    {
        private readonly AbstractPager _journalPager;
        private readonly AbstractPager _dataPager;
        private readonly AbstractPager _recoveryPager;

        private readonly long _lastSyncedTransactionId;
        private long _readAt4Kb;
        private readonly DiffApplier _diffApplier = new DiffApplier();
        private readonly long _journalPagerNumberOfAllocated4Kb;


        public bool RequireHeaderUpdate { get; private set; }

        public long Next4Kb => _readAt4Kb;

        public JournalReader(AbstractPager journalPager, AbstractPager dataPager, AbstractPager recoveryPager,
            long lastSyncedTransactionId, TransactionHeader* previous)
        {
            RequireHeaderUpdate = false;
            _journalPager = journalPager;
            _dataPager = dataPager;
            _recoveryPager = recoveryPager;
            _lastSyncedTransactionId = lastSyncedTransactionId;
            _readAt4Kb = 0;
            LastTransactionHeader = previous;
            _journalPagerNumberOfAllocated4Kb = 
                _journalPager.TotalAllocationSize /(4*Constants.Size.Kilobyte);
        }

        public TransactionHeader* LastTransactionHeader { get; private set; }

        public bool ReadOneTransactionToDataFile(StorageEnvironmentOptions options)
        {
            if (_readAt4Kb >= _journalPagerNumberOfAllocated4Kb)
                return false;

            TransactionHeader* current;
            if (TryReadAndValidateHeader(options, out current) == false)
            {
                var lastValid4Kb = _readAt4Kb;
                _readAt4Kb++;
                while (_readAt4Kb < _journalPagerNumberOfAllocated4Kb)
                {
                    if (TryReadAndValidateHeader(options, out current))
                    {
                        RequireHeaderUpdate = true;
                        break;
                    }
                    _readAt4Kb++;
                }

                _readAt4Kb = lastValid4Kb;
                return false;
            }

            var transactionSizeIn4Kb =
                (current->CompressedSize + sizeof(TransactionHeader)) / (4*Constants.Size.Kilobyte) +
                ((current->CompressedSize + sizeof(TransactionHeader)) % (4*Constants.Size.Kilobyte) == 0 ? 0 : 1);


            if (current->TransactionId <= _lastSyncedTransactionId)
            {
                _readAt4Kb += transactionSizeIn4Kb;
                LastTransactionHeader = current;
                return true; // skipping
            }

            _readAt4Kb += transactionSizeIn4Kb;
            var numberOfPages = GetNumberOfPagesFor(current->UncompressedSize);
            _recoveryPager.EnsureContinuous(0, numberOfPages);
            _recoveryPager.EnsureMapped(this, 0, numberOfPages);
            var outputPage = _recoveryPager.AcquirePagePointer(this, 0);
            UnmanagedMemory.Set(outputPage, 0, (long)numberOfPages * Constants.Storage.PageSize);

            try
            {
                LZ4.Decode64LongBuffers((byte*)current + sizeof(TransactionHeader), current->CompressedSize, outputPage,
                    current->UncompressedSize, true);
            }
            catch (Exception e)
            {
                options.InvokeRecoveryError(this, "Could not de-compress, invalid data", e);
                RequireHeaderUpdate = true;

                return false;
            }

            var pageInfoPtr = (TransactionHeaderPageInfo*)outputPage;
            long totalRead = sizeof(TransactionHeaderPageInfo) * current->PageCount;
            if (totalRead > current->UncompressedSize)
                throw new InvalidDataException($"Attempted to read position {totalRead} from transaction data while the transaction is size {current->UncompressedSize}");

            for (var i = 0; i < current->PageCount; i++)
            {
                if(pageInfoPtr[i].PageNumber > current->LastPageNumber)
                    throw new InvalidDataException($"Transaction {current->TransactionId} contains refeence to page {pageInfoPtr[i].PageNumber} which is after the last allocated page {current->LastPageNumber}");
            }

            for (var i = 0; i < current->PageCount; i++)
            {
                if (totalRead > current->UncompressedSize)
                    throw new InvalidDataException($"Attempted to read position {totalRead} from transaction data while the transaction is size {current->UncompressedSize}");

                Debug.Assert(_journalPager.Disposed == false);
                Debug.Assert(_recoveryPager.Disposed == false);

                var numberOfPagesOnDestination = GetNumberOfPagesFor(pageInfoPtr[i].Size);
                _dataPager.EnsureContinuous(pageInfoPtr[i].PageNumber, numberOfPagesOnDestination);
                _dataPager.EnsureMapped(this, pageInfoPtr[i].PageNumber, numberOfPagesOnDestination);
                var pagePtr = _dataPager.AcquirePagePointer(this, pageInfoPtr[i].PageNumber);

                var diffPageNumber = *(long*)(outputPage + totalRead);
                if (pageInfoPtr[i].PageNumber != diffPageNumber)
                    throw new InvalidDataException($"Expected a diff for page {pageInfoPtr[i].PageNumber} but got one for {diffPageNumber}");
                totalRead += sizeof(long);

                _dataPager.UnprotectPageRange(pagePtr, (ulong)pageInfoPtr[i].Size);

                if (pageInfoPtr[i].DiffSize == 0)
                {
                    Memory.Copy(pagePtr, outputPage + totalRead, pageInfoPtr[i].Size);
                    totalRead += pageInfoPtr[i].Size;
                }
                else
                {
                    _diffApplier.Destination = pagePtr;
                    _diffApplier.Diff = outputPage + totalRead;
                    _diffApplier.Size = pageInfoPtr[i].Size;
                    _diffApplier.DiffSize = pageInfoPtr[i].DiffSize;
                    _diffApplier.Apply();
                    totalRead += pageInfoPtr[i].DiffSize;
                }

                _dataPager.ProtectPageRange(pagePtr, (ulong)pageInfoPtr[i].Size);
            }

            LastTransactionHeader = current;

            return true;
        }

        public void RecoverAndValidate(StorageEnvironmentOptions options)
        {
            while (ReadOneTransactionToDataFile(options))
            {
            }
        }

        public void SetStartPage(long value)
        {
            _readAt4Kb = value;
        }

        private bool TryReadAndValidateHeader(StorageEnvironmentOptions options, out TransactionHeader* current)
        {
            if (_readAt4Kb > _journalPagerNumberOfAllocated4Kb)
            {
                current = null;
                return false; // end of jouranl
            }

            const int pageTo4KbRatio = Constants.Storage.PageSize / (4 * Constants.Size.Kilobyte);
            var pageNumber = _readAt4Kb / pageTo4KbRatio;
            var positionInsidePage = (_readAt4Kb % pageTo4KbRatio) * (4 * Constants.Size.Kilobyte);

            current = (TransactionHeader*)
                (_journalPager.AcquirePagePointer(this, pageNumber) + positionInsidePage);

            // due to the reuse of journals we no longer can assume we have zeros in the end of the journal
            // we might have there random garbage or old transactions we can ignore, so we have the following scenarios:
            // * TxId <= current Id      ::  we can ignore old transaction of the reused journal and continue
            // * TxId == current Id + 1  ::  valid, but if hash is invalid. Transaction hasn't been committed
            // * TxId >  current Id + 1  ::  if hash is invalid we can ignore reused/random, but if hash valid then we might missed TXs 

            if (current->HeaderMarker != Constants.TransactionHeaderMarker)
            {
                // not a transaction page, 

                // if the header marker is zero or garbage, we are probably in the area at the end of the log file, and have no additional log records
                // to read from it. This can happen if the next transaction was too big to fit in the current log file. We stop reading
                // this log file and move to the next one, or it might have happened becuase of reuse of journal file 

                // note : we might encounter a "valid" TransactionHeaderMarker which is still garbage, so we will test that later on

                RequireHeaderUpdate = false;
                return false;
            }

            if (current->TransactionId < 0)
                return false;

            current = EnsureTransactionMapped(current, pageNumber, positionInsidePage);
            bool hashIsValid = ValidatePagesHash(options, current);

            if (LastTransactionHeader == null)
                return hashIsValid;

            var txIdDiff = current->TransactionId - LastTransactionHeader->TransactionId;

            // 1 is a first storage transaction which does not increment transaction counter after commit
            if (current->TransactionId != 1)
            {
                if (txIdDiff < 0)
                    return false;

                if (txIdDiff > 1 || txIdDiff == 0)
                {
                    if (hashIsValid)
                    {
                        // TxId is bigger then the last one by nore the '1' but has valid hash which mean we lost transactions in the middle
                        throw new InvalidDataException(
                            $"Transaction has valid(!) hash with invalid transaction id {current->TransactionId}, the last valid transaction id is {LastTransactionHeader->TransactionId}." +
                            $" Journal file {_journalPager.FileName} might be corrupted");
                    }
                }

                // if (txIdDiff == 1) :
                if (current->LastPageNumber <= 0)
                    throw new InvalidDataException("Last page number after committed transaction must be greater than 0");
            }

            if (hashIsValid == false)
            {
                RequireHeaderUpdate = true;
                options.InvokeRecoveryError(this, "Transaction " + current->TransactionId + " was not committed",
                    null);
                return false;
            }

            return true;
        }

        private TransactionHeader* EnsureTransactionMapped(TransactionHeader* current, long pageNumber, long positionInsidePage)
        {
            var numberOfPages = GetNumberOfPagesFor(positionInsidePage + sizeof(TransactionHeader) + current->CompressedSize);
            _journalPager.EnsureMapped(this, pageNumber, numberOfPages);

            var pageHeader = _journalPager.AcquirePagePointer(this, pageNumber)
                             + positionInsidePage;

            return (TransactionHeader*)pageHeader;
        }
        
        private bool ValidatePagesHash(StorageEnvironmentOptions options, TransactionHeader* current)
        {
            byte* dataPtr = (byte*)current + sizeof(TransactionHeader);

            if (current->CompressedSize < 0)
            {
                RequireHeaderUpdate = true;
                // negative size is not supported
                options.InvokeRecoveryError(this, $"Compresses size {current->CompressedSize} is negative", null);
                return false;
            }
            if (current->CompressedSize >
                (_journalPagerNumberOfAllocated4Kb - _readAt4Kb) * 4 * Constants.Size.Kilobyte)
            {
                // we can't read past the end of the journal
                RequireHeaderUpdate = true;
                options.InvokeRecoveryError(this, $"Compresses size {current->CompressedSize} is too big for the journal size {_journalPagerNumberOfAllocated4Kb * 4 * Constants.Size.Kilobyte}", null);
                return false;
            }

            ulong hash = Hashing.XXHash64.Calculate(dataPtr, (ulong)current->CompressedSize, (ulong)current->TransactionId);
            if (hash != current->Hash)
            {
                RequireHeaderUpdate = true;
                options.InvokeRecoveryError(this, "Invalid hash signature for transaction: " + current->ToString(), null);

                return false;
            }
            return true;
        }

        public override string ToString()
        {
            return _journalPager.ToString();
        }

        public void Dispose()
        {
            OnDispose?.Invoke(this);
        }

        private static int GetNumberOfPagesFor(long size)
        {
            return checked((int)(size / Constants.Storage.PageSize) + (size % Constants.Storage.PageSize == 0 ? 0 : 1));
        }

        Dictionary<AbstractPager, Windows32BitMemoryMapPager.TransactionState> IPagerLevelTransactionState.
            Windows32BitPagerTransactionState
        { get; set; }

        public event Action<IPagerLevelTransactionState> OnDispose;

        void IPagerLevelTransactionState.EnsurePagerStateReference(PagerState state)
        {
            //nothing to do
        }

        StorageEnvironment IPagerLevelTransactionState.Environment => null;// not setup yet
    }
}
