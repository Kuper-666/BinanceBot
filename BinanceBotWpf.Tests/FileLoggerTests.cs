using System;
using System.IO;
using BinanceBotWpf.Services;
using Xunit;

namespace BinanceBotWpf.Tests
{
    public class FileLoggerTests : IDisposable
    {
        private readonly string _testDir;

        public FileLoggerTests ()
        {
            _testDir = Path.Combine (Path.GetTempPath (), $"FileLoggerTest_{Guid.NewGuid ():N}");
            Directory.CreateDirectory (_testDir);
        }

        public void Dispose ()
        {
            try { Directory.Delete (_testDir, true); } catch { }
        }

        private string ReadLogFile ()
        {
            string[] files = Directory.GetFiles (_testDir, "bot_*.log");
            Assert.True (files.Length > 0, "No log file created");
            return File.ReadAllText (files[0]);
        }

        [Fact]
        public void Log_CreatesLogFile ()
        {
            var logger = new FileLogger (_testDir);
            logger.Error ("Test", "Hello");
            logger.Dispose ();

            string[] files = Directory.GetFiles (_testDir, "bot_*.log");
            Assert.Single (files);
        }

        [Fact]
        public void Log_WritesContentToFile ()
        {
            var logger = new FileLogger (_testDir);
            logger.Error ("Test", "Message123");
            logger.Dispose ();

            string content = ReadLogFile ();
            Assert.Contains ("Message123", content);
            Assert.Contains ("ERROR", content);
            Assert.Contains ("Test", content);
        }

        [Fact]
        public void Log_WritesTimestamp ()
        {
            var logger = new FileLogger (_testDir);
            logger.Error ("Src", "Warning");
            logger.Dispose ();

            string content = ReadLogFile ();
            Assert.Matches (@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}", content);
        }

        [Fact]
        public void Log_Error_WritesErrorLevel ()
        {
            var logger = new FileLogger (_testDir);
            logger.Error ("Src", "Error occurred");
            logger.Dispose ();

            string content = ReadLogFile ();
            Assert.Contains ("[ERROR]", content);
        }

        [Fact]
        public void Log_MultipleCalls_AllWritten ()
        {
            var logger = new FileLogger (_testDir);
            logger.Error ("A", "First");
            logger.Error ("B", "Second");
            logger.Error ("C", "Third");
            logger.Dispose ();

            string content = ReadLogFile ();
            Assert.Contains ("First", content);
            Assert.Contains ("Second", content);
            Assert.Contains ("Third", content);
        }

        [Fact]
        public void Log_InfoAndWarn_NotWritten ()
        {
            var logger = new FileLogger (_testDir);
            logger.Info ("Test", "info message");
            logger.Warn ("Test", "warn message");
            logger.Dispose ();

            string[] files = Directory.GetFiles (_testDir, "bot_*.log");
            if (files.Length > 0)
            {
                string content = File.ReadAllText (files[0]);
                Assert.DoesNotContain ("info message", content);
                Assert.DoesNotContain ("warn message", content);
            }
        }

        [Fact]
        public void Dispose_CanBeCalledMultipleTimes ()
        {
            var logger = new FileLogger (_testDir);
            logger.Error ("Test", "data");
            logger.Dispose ();
            logger.Dispose ();
        }

        [Fact]
        public void Log_AfterDispose_DoesNotThrow ()
        {
            var logger = new FileLogger (_testDir);
            logger.Dispose ();
            logger.Error ("Test", "should not throw");
        }
    }
}
