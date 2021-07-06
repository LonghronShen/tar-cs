using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace tar_cs
{
    /// <summary>
    /// Extract contents of a tar file represented by a stream for the TarReader constructor
    /// </summary>
    public class TarReader
        : IDisposable
    {
        private readonly byte[] dataBuffer = new byte[512];
        private readonly UsTarHeader header;
        private readonly Stream inStream;
        private long remainingBytesInFile;
        private bool disposedValue;

        /// <summary>
        /// Constructs TarReader object to read data from `tarredData` stream
        /// </summary>
        /// <param name="tarredData">A stream to read tar archive from</param>
        public TarReader(Stream tarredData)
        {
            inStream = tarredData ?? throw new ArgumentException(nameof(tarredData));
            header = new UsTarHeader();
        }

        public ITarHeader FileInfo
        {
            get { return header; }
        }

        /// <summary>
        /// Read all files from an archive to a directory. It creates some child directories to
        /// reproduce a file structure from the archive.
        /// </summary>
        /// <param name="destDirectory">The out directory.</param>
        /// 
        /// CAUTION! This method is not safe. It's not tar-bomb proof. 
        /// {see http://en.wikipedia.org/wiki/Tar_(file_format) }
        /// If you are not sure about the source of an archive you extracting,
        /// then use MoveNext and Read and handle paths like ".." and "../.." according
        /// to your business logic.
        public async Task ReadToEndAsync(string destDirectory)
        {
            while (await MoveNextAsync(false))
            {
                string fileNameFromArchive = FileInfo.FileName;
                string totalPath = destDirectory + Path.DirectorySeparatorChar + fileNameFromArchive;
                if (UsTarHeader.IsPathSeparator(fileNameFromArchive[fileNameFromArchive.Length - 1]) || FileInfo.EntryType == EntryType.Directory)
                {
                    // Record is a directory
                    Directory.CreateDirectory(totalPath);
                    continue;
                }
                // If record is a file
                string fileName = Path.GetFileName(totalPath);
                string directory = totalPath.Remove(totalPath.Length - fileName.Length);
                Directory.CreateDirectory(directory);
                using (FileStream file = File.Create(totalPath))
                {
                    await ReadAsync(file);
                }
            }
        }

        public async Task ForEachAsync(Func<bool, string, Func<Stream, Task>, Task<bool>> processor)
        {
            async Task<bool> LocalProcessAsync(bool isDirectory, string path, Func<Stream, Task> streamWriter)
            {
                if (processor == null)
                {
                    return false;
                }
                return await processor(isDirectory, path, streamWriter);
            }

            while (await MoveNextAsync(false))
            {
                string fileNameFromArchive = FileInfo.FileName;
                if (UsTarHeader.IsPathSeparator(fileNameFromArchive[fileNameFromArchive.Length - 1]) ||
                    FileInfo.EntryType == EntryType.Directory)
                {
                    // Record is a directory
                    if (!await LocalProcessAsync(true, fileNameFromArchive, null))
                    {
                        return;
                    }
                    continue;
                }
                // If record is a file
                if (!await LocalProcessAsync(false, fileNameFromArchive,
                    async (s) => await this.ReadAsync(s)))
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Read data from a current file to a Stream.
        /// </summary>
        /// <param name="dataDestanation">A stream to read data to</param>
        /// 
        /// <seealso cref="MoveNext"/>
        public async Task ReadAsync(Stream dataDestanation)
        {
            Debug.WriteLine("tar stream position Read in: " + inStream.Position);
            int readBytes;
            while ((readBytes = await ReadAsync(dataBuffer)) != -1)
            {
                Debug.WriteLine("tar stream position Read while(...) : " + inStream.Position);
                await dataDestanation.WriteAsync(dataBuffer, 0, readBytes);
            }
            Debug.WriteLine("tar stream position Read out: " + inStream.Position);
        }

        protected async Task<int> ReadAsync(byte[] buffer)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (remainingBytesInFile == 0)
            {
                return -1;
            }
            int align512 = -1;
            long toRead = remainingBytesInFile - 512;

            if (toRead > 0)
                toRead = 512;
            else
            {
                align512 = 512 - (int)remainingBytesInFile;
                toRead = remainingBytesInFile;
            }

            long bytesRemainingToRead = toRead;

            int bytesRead;
            do
            {
                bytesRead = await inStream.ReadAsync(buffer, (int)(toRead - bytesRemainingToRead), (int)bytesRemainingToRead);
                bytesRemainingToRead -= bytesRead;
                remainingBytesInFile -= bytesRead;
            } while (bytesRead < toRead && bytesRemainingToRead > 0);

            if (inStream.CanSeek && align512 > 0)
            {
                inStream.Seek(align512, SeekOrigin.Current);
            }
            else
            {
                while (align512 > 0)
                {
                    inStream.ReadByte();
                    --align512;
                }
            }

            return bytesRead;
        }

        /// <summary>
        /// Check if all bytes in buffer are zeroes
        /// </summary>
        /// <param name="buffer">buffer to check</param>
        /// <returns>true if all bytes are zeroes, otherwise false</returns>
        private static bool IsEmpty(IEnumerable<byte> buffer)
        {
            return !buffer.Any(x => x != 0);
        }

        /// <summary>
        /// Move internal pointer to a next file in archive.
        /// </summary>
        /// <param name="skipData">Should be true if you want to read a header only, otherwise false</param>
        /// <returns>false on End Of File otherwise true</returns>
        /// 
        /// Example:
        /// while(MoveNext())
        /// { 
        ///     Read(dataDestStream); 
        /// }
        /// <seealso cref="Read(Stream)"/>
        public async Task<bool> MoveNextAsync(bool skipData)
        {
            Debug.WriteLine("tar stream position MoveNext in: " + inStream.Position);
            if (remainingBytesInFile > 0)
            {
                if (!skipData)
                {
                    throw new TarException(
                        "You are trying to change file while not all the data from the previous one was read. If you do want to skip files use skipData parameter set to true.");
                }
                // Skip to the end of file.
                if (inStream.CanSeek)
                {
                    long remainer = (remainingBytesInFile % 512);
                    inStream.Seek(remainingBytesInFile + (512 - (remainer == 0 ? 512 : remainer)), SeekOrigin.Current);
                }
                else
                {
                    while (await ReadAsync(dataBuffer) > 0)
                    {
                    }
                }
            }

            byte[] bytes = header.GetBytes();
            int headerRead;
            int bytesRemaining = header.HeaderSize;
            do
            {
                headerRead = await inStream.ReadAsync(bytes, header.HeaderSize - bytesRemaining, bytesRemaining);
                bytesRemaining -= headerRead;
                if (headerRead <= 0 && bytesRemaining > 0)
                {
                    throw new TarException("Can not read header");
                }
            } while (bytesRemaining > 0);

            if (IsEmpty(bytes))
            {
                bytesRemaining = header.HeaderSize;
                do
                {
                    headerRead = inStream.Read(bytes, header.HeaderSize - bytesRemaining, bytesRemaining);
                    bytesRemaining -= headerRead;
                    if (headerRead <= 0 && bytesRemaining > 0)
                    {
                        throw new TarException("Broken archive");
                    }

                } while (bytesRemaining > 0);
                if (bytesRemaining == 0 && IsEmpty(bytes))
                {
                    Debug.WriteLine("tar stream position MoveNext  out(false): " + inStream.Position);
                    return false;
                }
                throw new TarException("Broken archive");
            }

            if (header.UpdateHeaderFromBytes())
            {
                throw new TarException("Checksum check failed");
            }

            remainingBytesInFile = header.SizeInBytes;

            Debug.WriteLine("tar stream position MoveNext  out(true): " + inStream.Position);
            return true;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                    this.inStream?.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~TarReader()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

    }

}