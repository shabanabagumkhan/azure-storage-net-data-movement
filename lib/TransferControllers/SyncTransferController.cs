//------------------------------------------------------------------------------
// <copyright file="SyncTransferController.cs" company="Microsoft">
//    Copyright (c) Microsoft Corporation
// </copyright>
//------------------------------------------------------------------------------

namespace Microsoft.WindowsAzure.Storage.DataMovement.TransferControllers
{
    using System;
    using System.Collections.Concurrent;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.WindowsAzure.Storage.Blob;

    internal class SyncTransferController : TransferControllerBase
    {
        private readonly TransferReaderWriterBase reader;
        private readonly TransferReaderWriterBase writer;

        public SyncTransferController(
            TransferScheduler transferScheduler,
            TransferJob transferJob,
            CancellationToken userCancellationToken)
            : base(transferScheduler, transferJob, userCancellationToken)
        {
            if (null == transferScheduler)
            {
                throw new ArgumentNullException(nameof(transferScheduler));
            }

            if (null == transferJob)
            {
                throw new ArgumentNullException(nameof(transferJob));
            }

            this.SharedTransferData = new SharedTransferData()
            {
                TransferJob = this.TransferJob,
                AvailableData = new ConcurrentDictionary<long, TransferData>(),
            };

            if (null == transferJob.CheckPoint)
            {
                transferJob.CheckPoint = new SingleObjectCheckpoint();
            }

            this.reader = this.GetReader(transferJob.Source);
            this.writer = this.GetWriter(transferJob.Destination);
            
            this.CheckAndEnableSmallFileOptimization();

            this.SharedTransferData.OnTotalLengthChanged += (sender, args) =>
            {
                // For large block blob uploading, we need to re-calculate the BlockSize according to the total size
                // The formula: Ceiling(TotalSize / (50000 * DefaultBlockSize)) * DefaultBlockSize. This will make sure the 
                // new block size will be mutiple of DefaultBlockSize(aka MemoryManager's chunk size)
                if (this.writer is BlockBlobWriter)
                {
                    var normalMaxBlockBlobSize = (long)50000 * Constants.DefaultBlockSize;

                    // Calculate the min block size according to the blob total length
                    var memoryChunksRequiredEachTime = (int)Math.Ceiling((double)this.SharedTransferData.TotalLength / normalMaxBlockBlobSize);
                    var blockSize = memoryChunksRequiredEachTime * Constants.DefaultBlockSize;

                    // Take the block size user specified when it's greater than the calculated value
                    if (TransferManager.Configurations.BlockSize > blockSize)
                    {
                        blockSize = TransferManager.Configurations.BlockSize;
                        memoryChunksRequiredEachTime = (int)Math.Ceiling((double)blockSize / Constants.DefaultBlockSize);
                    }
                    else
                    {
                        // Try to increase the memory pool size
                        this.Scheduler.TransferOptions.UpdateMaximumCacheSize(blockSize);
                    }

                    // If data size is smaller than block size, fit block size according to total length, in order to minimize buffer allocation,
                    // and save space and time.
                    if (this.SharedTransferData.TotalLength < blockSize)
                    {
                        // Note total length could be 0, in this case, use default block size.
                        memoryChunksRequiredEachTime = Math.Max(1,
                            (int)Math.Ceiling((double)this.SharedTransferData.TotalLength / Constants.DefaultBlockSize));
                        blockSize = memoryChunksRequiredEachTime * Constants.DefaultBlockSize;
                    }
                    this.SharedTransferData.BlockSize = blockSize;
                    this.SharedTransferData.MemoryChunksRequiredEachTime = memoryChunksRequiredEachTime;
                }
                else
                {
                    // For normal directions, we'll use default block size 4MB for transfer.
                    this.SharedTransferData.BlockSize = Constants.DefaultBlockSize;
                    this.SharedTransferData.MemoryChunksRequiredEachTime = 1;
                }
            };
        }

        public SharedTransferData SharedTransferData
        {
            get;
            private set;
        }

        public bool ErrorOccurred
        {
            get;
            private set;
        }

        public override bool HasWork
        {
            get
            {
                var hasWork = (!this.reader.PreProcessed && this.reader.HasWork)
                    || (this.reader.PreProcessed && this.writer.HasWork)
                    || (this.writer.PreProcessed && this.reader.HasWork);

                return !this.ErrorOccurred && hasWork;
            }
        }

        protected override async Task<bool> DoWorkInternalAsync()
        {
            if (!this.reader.PreProcessed && this.reader.HasWork)
            {
                await this.reader.DoWorkInternalAsync();
            }
            else if (this.reader.PreProcessed && this.writer.HasWork)
            {
                await this.writer.DoWorkInternalAsync();
            }
            else if (this.writer.PreProcessed && this.reader.HasWork)
            {
                await this.reader.DoWorkInternalAsync();
            }

            return this.ErrorOccurred || this.writer.IsFinished;
        }

        protected override void SetErrorState(Exception ex)
        {
            this.ErrorOccurred = true;
        }

        private TransferReaderWriterBase GetReader(TransferLocation sourceLocation)
        {
            switch (sourceLocation.Type)
            {
                case TransferLocationType.Stream:
                    return new StreamedReader(this.Scheduler, this, this.CancellationToken);
                case TransferLocationType.FilePath:
                    return new StreamedReader(this.Scheduler, this, this.CancellationToken);
                case TransferLocationType.AzureBlob:
                    CloudBlob sourceBlob = (sourceLocation as AzureBlobLocation).Blob;
                    if (sourceBlob is CloudPageBlob)
                    {
                        return new PageBlobReader(this.Scheduler, this, this.CancellationToken);
                    }
                    else if (sourceBlob is CloudBlockBlob)
                    {
                        return new BlockBasedBlobReader(this.Scheduler, this, this.CancellationToken);
                    }
                    else if (sourceBlob is CloudAppendBlob)
                    {
                        return new BlockBasedBlobReader(this.Scheduler, this, this.CancellationToken);
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            string.Format(
                            CultureInfo.CurrentCulture,
                            Resources.UnsupportedBlobTypeException,
                            sourceBlob.BlobType));
                    }
                case TransferLocationType.AzureFile:
                    return new CloudFileReader(this.Scheduler, this, this.CancellationToken);
                default:
                    throw new InvalidOperationException(
                        string.Format(
                        CultureInfo.CurrentCulture,
                        Resources.UnsupportedTransferLocationException,
                        sourceLocation.Type));
            }
        }

        private TransferReaderWriterBase GetWriter(TransferLocation destLocation)
        {
            switch (destLocation.Type)
            {
                case TransferLocationType.Stream:
                    return new StreamedWriter(this.Scheduler, this, this.CancellationToken);
                case TransferLocationType.FilePath:
                    return new StreamedWriter(this.Scheduler, this, this.CancellationToken);
                case TransferLocationType.AzureBlob:
                    CloudBlob destBlob = (destLocation as AzureBlobLocation).Blob;
                    if (destBlob is CloudPageBlob)
                    {
                        return new PageBlobWriter(this.Scheduler, this, this.CancellationToken);
                    }
                    else if (destBlob is CloudBlockBlob)
                    {
                        return new BlockBlobWriter(this.Scheduler, this, this.CancellationToken);
                    }
                    else if (destBlob is CloudAppendBlob)
                    {
                        return new AppendBlobWriter(this.Scheduler, this, this.CancellationToken);
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            string.Format(
                            CultureInfo.CurrentCulture,
                            Resources.UnsupportedBlobTypeException,
                            destBlob.BlobType));
                    }
                case TransferLocationType.AzureFile:
                    return new CloudFileWriter(this.Scheduler, this, this.CancellationToken);
                default:
                    throw new InvalidOperationException(
                        string.Format(
                        CultureInfo.CurrentCulture,
                        Resources.UnsupportedTransferLocationException,
                        destLocation.Type));
            }
        }

        /// <summary>
        /// Currently only do small file optimization for block/append blob download, and block blob upload.
        /// Further small file optimization would be done according to feedbacks.
        /// </summary>
        private void CheckAndEnableSmallFileOptimization()
        {
            if ((this.reader is BlockBasedBlobReader && this.writer is StreamedWriter) ||
                (this.reader is StreamedReader && this.writer is BlockBlobWriter))
            {
                this.reader.EnableSmallFileOptimization = true;
                this.writer.EnableSmallFileOptimization = true;
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                this.reader?.Dispose();

                this.writer?.Dispose();

                foreach (var transferData in this.SharedTransferData.AvailableData.Values)
                {
                    transferData.Dispose();
                }

                this.SharedTransferData.AvailableData.Clear();
            }
        }
    }
}
