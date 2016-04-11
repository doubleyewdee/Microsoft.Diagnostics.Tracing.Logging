﻿// The MIT License (MIT)
// 
// Copyright (c) 2015 Microsoft
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

namespace Microsoft.Diagnostics.Tracing.Logging.UnitTests
{
    using System;
    using System.Diagnostics.Tracing;
    using System.IO;
    using System.Threading;

    using NUnit.Framework;

    [TestFixture]
    public class ManagerTests
    {
        [Test]
        public void CheckedFileLoggerRotation()
        {
            // Create a file with a 5 minute rotation time, then attempt to rotate it every minute and ensure it
            // only changes names once.
            DateTime now = DateTime.UtcNow;
            using (var utcLogger = new FileBackedLogger(
                new LogConfiguration("utctime", LogType.Text)
                {
                    Directory = ".",
                    RotationInterval = 300,
                    TimestampLocal = false,
                }))
            {
                using (var localLogger = new FileBackedLogger(
                    new LogConfiguration("loctime", LogType.Text)
                    {
                        Directory = ".",
                        RotationInterval = 300,
                        TimestampLocal = true,
                    }))
                {
                    Assert.IsNotNull(utcLogger);
                    utcLogger.CheckedRotate(now);
                    string currentUtcFilename = utcLogger.Logger.Filename;
                    int utcFilenameChanges = 0;

                    Assert.IsNotNull(localLogger);
                    localLogger.CheckedRotate(now);
                    string currentLocalFilename = localLogger.Logger.Filename;
                    int localFilenameChanges = 0;

                    for (int i = 0; i < 5; ++i)
                    {
                        now += new TimeSpan(0, 1, 0);
                        utcLogger.CheckedRotate(now);
                        localLogger.CheckedRotate(now);

                        if (
                            string.Compare(currentUtcFilename, utcLogger.Logger.Filename,
                                           StringComparison.OrdinalIgnoreCase) !=
                            0)
                        {
                            ++utcFilenameChanges;
                            currentUtcFilename = utcLogger.Logger.Filename;
                        }

                        if (
                            string.Compare(currentLocalFilename, localLogger.Logger.Filename,
                                           StringComparison.OrdinalIgnoreCase) !=
                            0)
                        {
                            ++localFilenameChanges;
                            currentLocalFilename = localLogger.Logger.Filename;
                        }
                    }

                    Assert.AreEqual(1, utcFilenameChanges, "UTC timestamp filename changed more than once.");
                    Assert.AreEqual(1, localFilenameChanges, "Local timestamp filename changed more than once.");
                }
            }
        }

        [Test]
        public void CreateFileBackedLogger()
        {
            LogManager.Start();
            try
            {
                new FileBackedLogger(new LogConfiguration("badlogger", LogType.MemoryBuffer));
                Assert.Fail();
            }
            catch (ArgumentException) { }

            using (var logger = new FileBackedLogger(
                new LogConfiguration("testfile", LogType.Text)
                {
                    Directory = Path.GetFullPath("."),
                    RotationInterval = 0,
                    FilenameTemplate = "{0}"
                }))
            {
                Assert.IsNotNull(logger.Logger);
                Assert.AreEqual(0, logger.RotationInterval);
                string fullFilename = (logger.Logger as TextFileLogger).Filename;
                string dName = Path.GetDirectoryName(fullFilename);
                Assert.AreEqual(dName, Path.GetFullPath("."));
                string fName = Path.GetFileName(fullFilename);
                Assert.AreEqual(fName, "testfile.log");
            }

            LogManager.Shutdown();
        }

        [Test]
        public void DefaultDirectories()
        {
            string oldDataDir = Environment.GetEnvironmentVariable(LogManager.DataDirectoryEnvironmentVariable);

            LogManager.Shutdown();
            Environment.SetEnvironmentVariable(LogManager.DataDirectoryEnvironmentVariable, null);
            LogManager.Start();
            string expectedRoot = Path.GetFullPath(".\\logs");
            Assert.AreEqual(expectedRoot, LogManager.DefaultDirectory);
            // root / default directories have been combined.
            Assert.AreEqual(LogManager.DefaultDirectory, LogManager.DefaultDirectory);

            // unrooted DATADIR is ignored.
            LogManager.Shutdown();
            Environment.SetEnvironmentVariable(LogManager.DataDirectoryEnvironmentVariable, "unrooted");
            LogManager.Start();
            Assert.AreEqual(expectedRoot, LogManager.DefaultDirectory);
            // root / default directories have been combined.
            Assert.AreEqual(LogManager.DefaultDirectory, LogManager.DefaultDirectory);

            LogManager.Shutdown();
            Environment.SetEnvironmentVariable(LogManager.DataDirectoryEnvironmentVariable, "c:\\tmp");
            LogManager.Start();
            expectedRoot = "c:\\tmp\\logs";
            Assert.AreEqual(expectedRoot, LogManager.DefaultDirectory);
            // root / default directories have been combined.
            Assert.AreEqual(LogManager.DefaultDirectory, LogManager.DefaultDirectory);

            LogManager.Shutdown();
            Environment.SetEnvironmentVariable(LogManager.DataDirectoryEnvironmentVariable, oldDataDir);
        }

        [Test]
        public void FileLoggerLocalTime()
        {
            using (var localTimeLogger = new FileBackedLogger(
                new LogConfiguration("loctime", LogType.Text)
                {
                    Directory = ".",
                    RotationInterval = LogManager.MinRotationInterval,
                    TimestampLocal = true,
                }))
            {
                using (var utcTimeLogger = new FileBackedLogger(
                    new LogConfiguration("utctime", LogType.Text)
                    {
                        Directory = ".",
                        RotationInterval = LogManager.MinRotationInterval,
                        TimestampLocal = false,
                    }))
                {
                    Assert.AreEqual(localTimeLogger.FilenameTemplate,
                                    LogManager.DefaultLocalTimeFilenameTemplate + FileBackedLogger.TextLogExtension);
                    Assert.AreEqual(utcTimeLogger.FilenameTemplate,
                                    LogManager.DefaultFilenameTemplate + FileBackedLogger.TextLogExtension);
                    // local time filename should be longer. (has timezone)
                    Assert.IsTrue(localTimeLogger.Logger.Filename.Length > utcTimeLogger.Logger.Filename.Length);
                }
            }
        }

        [Test]
        public void LogWrangling()
        {
            LogManager.Start();
            LogManager.SetConfiguration(""); // wipe any config

            Assert.IsNotNull(LogManager.ConsoleLogger);
            try
            {
                LogManager.DestroyLogger(LogManager.ConsoleLogger);
                Assert.Fail();
            }
            catch (ArgumentException) { }

            var someLogger = LogManager.CreateLogger<TextFileLogger>(new LogConfiguration("testlog", LogType.Text));
            Assert.IsNotNull(someLogger);
            Assert.AreSame(someLogger, LogManager.GetLogger<TextFileLogger>("testlog"));
            LogManager.DestroyLogger(someLogger);
            Assert.IsNull(LogManager.GetLogger<TextFileLogger>("testlog"));

            FileBackedLogger externalLogger = null;
            try
            {
                externalLogger = new FileBackedLogger(new LogConfiguration("external", LogType.Text) {Directory = "."});
                LogManager.DestroyLogger(externalLogger.Logger);
                Assert.Fail();
            }
            catch (ArgumentException) { }
            finally
            {
                externalLogger?.Dispose();
            }
            LogManager.Shutdown();
        }

        [Test]
        public void ManualFileLoggerRotation()
        {
            Assert.IsTrue(LogManager.MinDemandRotationDelta <= LogManager.MinRotationInterval,
                          "Error in modified constants. Rotation will not work as expected.");

            // Create a file with a large rotation time, then ensure manual rotation occurs.
            LogManager.Start();
            LogManager.SetConfiguration(""); // wipe any config
            using (var logger = LogManager.CreateLogger<TextFileLogger>(new LogConfiguration("testfile", LogType.Text)
            {
                Directory = ".",
                RotationInterval = LogManager.MaxRotationInterval
            }))
            {
                Assert.IsNotNull(logger);
                // Subscribe to our internal events to catch cases where file rotation would try to write a message
                // when the backing store isn't available.
                logger.SubscribeToEvents(InternalLogger.Write, EventLevel.Verbose);

                string currentFilename = logger.Filename;
                Thread.Sleep(1000); // Default template includes seconds so wait at least one.
                Assert.IsTrue(LogManager.RotateFiles());
                string newFilename = logger.Filename;
                Assert.IsFalse(LogManager.RotateFiles()); // try to double rotate, limiter should stop us
                Assert.AreEqual(newFilename, logger.Filename);
                Assert.AreNotEqual(currentFilename, newFilename);
                currentFilename = newFilename;

                // Now wait to be allowed to rotate again.
                Thread.Sleep(LogManager.MinDemandRotationDelta * 1000);
                Assert.IsTrue(LogManager.RotateFiles());
                newFilename = logger.Filename;
                Assert.AreNotEqual(currentFilename, newFilename);
            }
            LogManager.Shutdown();
        }

        [Test]
        public void Miscellaneous()
        {
            // this is here because it really is bad to change this value. Hopefully anybody who changes it runs the
            // test and thinks twice.
            LogManager.Start();
            Assert.AreEqual(60, LogManager.MinRotationInterval);
            try
            {
                LogManager.DefaultRotationInterval *= 8675309;
                Assert.Fail();
            }
            catch (ArgumentOutOfRangeException) { }
            LogManager.Shutdown();
        }
    }
}