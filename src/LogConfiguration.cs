// The MIT License (MIT)
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

namespace Microsoft.Diagnostics.Tracing.Logging
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.Tracing;

    /// <summary>
    /// A pair of level / keyword values describing what events from a source to consume.
    /// </summary>
    public struct LogSourceLevels
    {
        public readonly EventKeywords Keywords;
        public readonly EventLevel Level;

        public LogSourceLevels(EventLevel level, EventKeywords keywords)
        {
            this.Level = level;
            this.Keywords = keywords;
        }
    }

    /// <summary>
    /// Different varieties of log type.
    /// </summary>
    public enum LogType
    {
        /// <summary>
        /// No specified type (do not use).
        /// </summary>
        None,
        /// <summary>
        /// Console output log.
        /// </summary>
        Console,
        /// <summary>
        /// Memory buffer log.
        /// </summary>
        MemoryBuffer,
        /// <summary>
        /// Text log.
        /// </summary>
        Text,
        /// <summary>
        /// ETW log.
        /// </summary>
        EventTracing,
        /// <summary>
        /// Network (http) based log.
        /// </summary>
        Network
    }

    /// <summary>
    /// A small holder for the parsed out logging configuration of a single log
    /// </summary>
    public sealed class LogConfiguration
    {
        /// <summary>
        /// The set of capabilities an event log provides.
        /// </summary>
        [Flags]
        internal enum Features
        {
            None = 0x0,
            EventSourceSubscription = 0x1,
            GuidSubscription = 0x2,
            Unsubscription = 0x4,
            FileBacked = 0x8,
            RegexFilter = 0x10
        }

        public readonly HashSet<string> Filters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public readonly Dictionary<Guid, LogSourceLevels> GuidSources = new Dictionary<Guid, LogSourceLevels>();

        public readonly Dictionary<string, LogSourceLevels> NamedSources =
            new Dictionary<string, LogSourceLevels>(StringComparer.OrdinalIgnoreCase);

        public int BufferSize = LogManager.DefaultFileBufferSizeMB;
        public string Directory;
        public string FilenameTemplate = FileBackedLogger.DefaultFilenameTemplate;
        public LogType FileType = LogType.None;
        public string Hostname = string.Empty;
        public int Port;
        public int RotationInterval = -1;
        public bool TimestampLocal;

        internal bool HasFeature(Features flags)
        {
            Features caps;
            switch (this.FileType)
            {
            case LogType.Console:
            case LogType.MemoryBuffer:
            case LogType.Network:
                caps = (Features.EventSourceSubscription | Features.Unsubscription |
                        Features.RegexFilter);
                break;
            case LogType.Text:
                caps = (Features.EventSourceSubscription | Features.Unsubscription |
                        Features.FileBacked | Features.RegexFilter);
                break;
            case LogType.EventTracing:
                caps = (Features.EventSourceSubscription | Features.GuidSubscription | Features.FileBacked);
                break;
            default:
                throw new InvalidOperationException("features for type " + this.FileType + " are unknowable");
            }

            return ((caps & flags) != 0);
        }
    }
}