using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace tar_cs.Tests
{

    public class TarTests
    {

        private readonly ITestOutputHelper _testOutputHelper;

        public TarTests(ITestOutputHelper testOutputHelper)
        {
            this._testOutputHelper = testOutputHelper;
        }

        public static Stream BuildSampleStream()
        {
            return new MemoryStream(new byte[] { 0x1, 0x2, 0x3 });
        }

        public static async Task<byte[]> BuildSampleTarAsync()
        {
            using var file = BuildSampleStream();
            using var ms = new MemoryStream();
            using var tw = new TarWriter(ms);
            await tw.WriteAsync(file, file.Length, "test.bin");
            return ms.ToArray();
        }

        [Fact]
        public async Task BuildPackTests()
        {
            var sample = await BuildSampleTarAsync();
            Assert.True(sample.Length % 512 == 0);
        }

        [Fact]
        public async Task ReadPackTests()
        {
            using var fs = new MemoryStream(await BuildSampleTarAsync());
            using var tr = new TarReader(fs);
            await tr.ForEachAsync(async (isDirectory, filePath, streamWriter) =>
            {
                this._testOutputHelper.WriteLine("File path: " + filePath);
                this._testOutputHelper.WriteLine("Type: " + (isDirectory ? "Directory" : "File"));
                using var ms = new MemoryStream();
                await streamWriter(ms);
                ms.Seek(0, SeekOrigin.Begin);
                return true;
            });
        }

    }

}