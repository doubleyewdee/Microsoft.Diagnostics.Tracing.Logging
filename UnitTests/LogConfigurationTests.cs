// The MIT License (MIT)
// 
// Copyright (c) 2015-2016 Microsoft
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
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;

    using Microsoft.Diagnostics.Tracing.Parsers.FrameworkEventSource;

    using Newtonsoft.Json;

    using NUnit.Framework;

    [TestFixture]
    public sealed class LogConfigurationTests
    {
        private static IEnumerable<LogConfiguration> Configurations
        {
            get
            {
                // HACK: this works around NUnit issue #54: https://github.com/nunit/docs/issues/54
                if (LogManager.DefaultDirectory == null)
                {
                    LogManager.Start();
                }

                var subs = LogManager.DefaultSubscriptions;

                foreach (var value in Enum.GetValues(typeof(LogType)))
                {
                    var filters = new[] {".*", "abc", "123"};
                    var logType = (LogType)value;
                    switch (logType)
                    {
                    case LogType.None:
                        continue;
                    case LogType.EventTracing:
                        filters = new string[0];
                        break;
                    }

                    var config = new LogConfiguration("somelog", logType, subs, filters);
                    yield return config;

                    config.BufferSizeMB = LogManager.MinLogBufferSizeMB;
                    yield return config;
                    config.BufferSizeMB = LogManager.MaxLogBufferSizeMB;
                    yield return config;

                    if (logType.HasFeature(LogConfiguration.Features.FileBacked))
                    {
                        config.Directory = null;
                        yield return config;
                        config.Directory = "something";
                        yield return config;

                        config.FilenameTemplate = "{0}_{1:YYYYmmddHHMMSS}blorp{2:YYYYmmddHHMMSS}";
                        yield return config;
                        config.TimestampLocal = !config.TimestampLocal;
                        yield return config;

                        config.RotationInterval = LogManager.MinRotationInterval;
                        yield return config;
                        config.RotationInterval = LogManager.MaxRotationInterval;
                        yield return config;
                    }
                    else if (logType == LogType.Network)
                    {
                        config.Hostname = "a.ham";
                        yield return config;
                        config.Hostname = "a.burr";
                        yield return config;
                        config.Port = 867;
                        yield return config;
                        config.Port = 5309;
                        yield return config;
                    }
                }
            }
        }

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            LogManager.Start();
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            LogManager.Shutdown();
        }

        [Test, TestCaseSource(nameof(Configurations))]
        public void CanSerialize(LogConfiguration configuration)
        {
            var serializer = new JsonSerializer();
            var json = configuration.ToString(); // ToString returns the JSON representation itself.
            using (var reader = new StringReader(json))
            {
                using (var jsonReader = new JsonTextReader(reader))
                {
                    var deserialized = serializer.Deserialize<LogConfiguration>(jsonReader);
                    Assert.AreEqual(configuration, deserialized);
                    Assert.AreEqual(configuration.Name, deserialized.Name);
                    Assert.AreEqual(configuration.Type, deserialized.Type);
                    foreach (var sub in configuration.Subscriptions)
                    {
                        Assert.IsTrue(deserialized.Subscriptions.Contains(sub));
                    }
                    foreach (var filter in configuration.Filters)
                    {
                        Assert.IsTrue(deserialized.Filters.Contains(filter));
                    }
                    Assert.AreEqual(configuration.BufferSizeMB, deserialized.BufferSizeMB);
                    Assert.AreEqual(configuration.Directory, deserialized.Directory);
                    Assert.AreEqual(configuration.FilenameTemplate, deserialized.FilenameTemplate);
                    Assert.AreEqual(configuration.TimestampLocal, deserialized.TimestampLocal);
                    Assert.AreEqual(configuration.RotationInterval, deserialized.RotationInterval);
                    Assert.AreEqual(configuration.Hostname, deserialized.Hostname);
                    Assert.AreEqual(configuration.Port, deserialized.Port);
                }
            }
        }

        public struct ConstructorArgs
        {
            public bool IsValid { get; }
            public string Name { get; }
            public LogType Type { get; }
            public IEnumerable<EventProviderSubscription> Subs { get; }
            public IEnumerable<string> Filters { get; }

            public ConstructorArgs(bool valid, string name, LogType type, IEnumerable<EventProviderSubscription> subs, IEnumerable<string> filters)
            {
                this.IsValid = valid;
                this.Name = name;
                this.Type = type;
                this.Subs = subs;
                this.Filters = filters;
            }

            public override string ToString()
            {
                return (this.IsValid ? "Valid" : "Invalid") +
                       " Name: " + (this.Name ?? "<null>") +
                       " Type: " + this.Type +
                       " Subs: " + (this.Subs?.Count().ToString() ?? "null") +
                       " Filters: " + (this.Filters?.Count().ToString() ?? "null");
            }
        }

        private static IEnumerable<ConstructorArgs> Constructors
        {
            get
            {
                foreach (var value in Enum.GetValues(typeof(LogType)))
                {
                    var logType = (LogType)value;
                    if (logType == LogType.None)
                    {
                        yield return new ConstructorArgs(false, null, logType, LogManager.DefaultSubscriptions, null);
                        continue;
                    }

                    yield return new ConstructorArgs(false, null, logType, null, null);
                    yield return new ConstructorArgs(false, null, logType, null, new[] {"abc", "123"});
                    yield return
                        new ConstructorArgs(logType == LogType.Console || logType == LogType.MemoryBuffer, null, logType,
                                            LogManager.DefaultSubscriptions, null);
                    yield return new ConstructorArgs(true, "foo", logType, LogManager.DefaultSubscriptions, null);

                    if (logType.HasFeature(LogConfiguration.Features.RegexFilter))
                    {
                        yield return
                            new ConstructorArgs(false, "foo", logType, LogManager.DefaultSubscriptions,
                                                new[] {"abc", "abc"});
                        yield return
                            new ConstructorArgs(true, "foo", logType, LogManager.DefaultSubscriptions,
                                                new[] {"abc", "123"});
                    }
                    if (logType.HasFeature(LogConfiguration.Features.FileBacked))
                    {
                        yield return
                            new ConstructorArgs(false, "foo" + string.Join("", Path.GetInvalidFileNameChars()), logType,
                                                LogManager.DefaultSubscriptions, null);
                    }
                }
            }
        }

        [Test, TestCaseSource(nameof(Constructors))]
        public void ConstructorValidation(ConstructorArgs args)
        {
            if (args.IsValid)
            {
                Assert.IsNotNull(new LogConfiguration(args.Name, args.Type, args.Subs, args.Filters));
            }
            else
            {
                Assert.Throws<InvalidConfigurationException>(
                                                             () =>
                                                             new LogConfiguration(args.Name, args.Type, args.Subs,
                                                                                  args.Filters));
            }
        }

        [Test]
        public void TypeSpecificPropertyModificationsValidated()
        {
            var filePropertyChanges = new Action<LogConfiguration>[]
                                      {
                                          config => config.Directory = "somedir",
                                          config => config.RotationInterval = LogManager.DefaultRotationInterval,
                                          config => config.FilenameTemplate = "{0}",
                                      };

            var networkPropertyChanges = new Action<LogConfiguration>[]
                                         {
                                             config => config.Hostname = "foo",
                                             config => config.Port = 5309,
                                         };

            foreach (var value in Enum.GetValues(typeof(LogType)))
            {
                var logType = (LogType)value;
                if (logType == LogType.None) continue;

                var config = new LogConfiguration("foo", logType, LogManager.DefaultSubscriptions);
                switch (logType)
                {
                case LogType.Console:
                case LogType.MemoryBuffer:
                    foreach (var expr in filePropertyChanges)
                    {
                        Assert.Throws<InvalidConfigurationException>(() => expr(config));
                    }
                    foreach (var expr in networkPropertyChanges)
                    {
                        Assert.Throws<InvalidConfigurationException>(() => expr(config));
                    }
                    break;
                case LogType.Text:
                case LogType.EventTracing:
                    foreach (var expr in filePropertyChanges)
                    {
                        Assert.DoesNotThrow(() => expr(config));
                    }
                    foreach (var expr in networkPropertyChanges)
                    {
                        Assert.Throws<InvalidConfigurationException>(() => expr(config));
                    }
                    break;
                case LogType.Network:
                    foreach (var expr in networkPropertyChanges)
                    {
                        Assert.DoesNotThrow(() => expr(config));
                    }
                    foreach (var expr in filePropertyChanges)
                    {
                        Assert.Throws<InvalidConfigurationException>(() => expr(config));
                    }
                    break;
                }
            }
        }

        [Test]
        public void NetworkPropertiesRequiredForValidConfiguration()
        {
            var config = new LogConfiguration("net", LogType.Network, LogManager.DefaultSubscriptions);
            Assert.IsFalse(config.IsValid);
            config.Hostname = "foo";
            Assert.IsFalse(config.IsValid);

            config = new LogConfiguration("net", LogType.Network, LogManager.DefaultSubscriptions);
            config.Port = 5309;
            Assert.IsFalse(config.IsValid);
            config.Hostname = "foo";
            Assert.IsTrue(config.IsValid);
        }
    }
}