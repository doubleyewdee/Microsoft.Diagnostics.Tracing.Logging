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
    using System.Globalization;
    using System.IO;

    /// <summary>
    /// A pair of level / keyword values describing what events from a source to consume.
    /// </summary>
    public struct LogSource
    {
        public readonly string Name;
        public readonly Guid Guid;
        public readonly EventKeywords Keywords;
        public readonly EventLevel Level;

        public LogSource(string name, EventLevel level, EventKeywords keywords)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidLogConfigurationException("Name may not be null or empty.");
            }
            this.Name = name;
            this.Guid = Guid.Empty;
            this.Level = level;
            this.Keywords = keywords;
        }

        public LogSource(Guid guid, EventLevel level, EventKeywords keywords)
        {
            if (guid == Guid.Empty)
            {
                throw new InvalidLogConfigurationException("Provider GUID must not be empty.");
            }

            this.Name = null;
            this.Guid = guid;
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

    public sealed class InvalidLogConfigurationException : Exception
    {
        public InvalidLogConfigurationException() { }

        public InvalidLogConfigurationException(string message) : base(message) { }

        public InvalidLogConfigurationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// A small holder for the parsed out logging configuration of a single log
    /// </summary>
    public sealed class LogConfiguration
    {
        private readonly Dictionary<Guid, LogSource> guidSources = new Dictionary<Guid, LogSource>();

        private readonly Dictionary<string, LogSource> namedSources =
            new Dictionary<string, LogSource>(StringComparer.OrdinalIgnoreCase);

        private int bufferSizeMB;
        private string directory;
        private string filenameTemplate;
        private string hostname;

        private IEventLogger logger;
        private ushort port;
        private int rotationInterval;

        public LogConfiguration(string name, LogType logType)
        {
            if (logType == LogType.None || !Enum.IsDefined(typeof(LogType), logType))
            {
                throw new InvalidLogConfigurationException("Log type ${logType} is invalid.");
            }
            this.Type = logType;

            switch (logType)
            {
            case LogType.Console:
            case LogType.MemoryBuffer:
                break;
            default:
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new InvalidLogConfigurationException("Log name must be specified.");
                }
                break;
            }
            if (this.HasFeature(Features.FileBacked) && name.IndexOfAny(Path.GetInvalidFileNameChars()) != -1)
            {
                throw new InvalidLogConfigurationException("base name ${name} of log is invalid");
            }

            // We use a special name for the console logger that is invalid for file loggers so we can track
            // it along with them.
            this.Name = logType == LogType.Console ? LogManager.ConsoleLoggerName : name;

            this.BufferSizeMB = LogManager.DefaultFileBufferSizeMB;
            this.directory = LogManager.DefaultDirectory;
            this.filenameTemplate = LogManager.DefaultFilenameTemplate;
            this.rotationInterval = LogManager.DefaultRotate ? LogManager.DefaultRotationInterval : 0;
            this.Filters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The name of the log.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The type of the log.
        /// </summary>
        public LogType Type { get; }

        /// <summary>
        /// Directory to emit logs to. When set the directory may be relative (in which case it will be qualified using the LogManager).
        /// </summary>
        public string Directory
        {
            get { return this.directory; }
            set
            {
                if (!this.HasFeature(Features.FileBacked))
                {
                    throw new InvalidLogConfigurationException("Directories are not valid for non-file loggers.");
                }
                try
                {
                    this.directory = LogManager.GetQualifiedDirectory(value);
                }
                catch (ArgumentException e)
                {
                    throw new InvalidLogConfigurationException($"Directory {value} is not valid.", e);
                }
            }
        }

        /// <summary>
        /// Template to use when rotating files.
        /// </summary>
        public string FilenameTemplate
        {
            get
            {
                // The rest of the library callers are built to pass in our constant for filename template, ignoring
                // whether they want local timestamps or not. Keep the logic here. We do ReferenceEquals because we
                // expect external callers who are passing in their own templates to put in the right format if they
                // want local time.
                if (this.TimestampLocal && ReferenceEquals(this.filenameTemplate, LogManager.DefaultFilenameTemplate))
                {
                    return LogManager.DefaultLocalTimeFilenameTemplate;
                }

                return this.filenameTemplate;
            }
            set
            {
                if (!this.HasFeature(Features.FileBacked))
                {
                    throw new InvalidLogConfigurationException("Filename templates are not valid for non-file loggers.");
                }
                if (!FileBackedLogger.IsValidFilenameTemplate(value))
                {
                    throw new InvalidLogConfigurationException($"Filename template '{value}' is invalid.");
                }

                this.filenameTemplate = value;
            }
        }

        public string Hostname
        {
            get { return this.hostname; }
            set
            {
                if (this.Type != LogType.Network)
                {
                    throw new InvalidLogConfigurationException("Hostnames are not valid for non-network loggers.");
                }
                if (Uri.CheckHostName(value) == UriHostNameType.Unknown)
                {
                    InternalLogger.Write.InvalidConfiguration($"invalid hostname '{value}'");
                }

                this.hostname = value;
            }
        }

        public ushort Port
        {
            get { return this.port; }
            set
            {
                if (this.Type != LogType.Network)
                {
                    throw new InvalidLogConfigurationException("Ports are not valid for non-network loggers.");
                }
                if (value == 0)
                {
                    throw new InvalidLogConfigurationException($"Port {value} is invalid.");
                }

                this.port = value;
            }
        }

        /// <summary>
        /// The interval in seconds to perform rotation on file loggers. Must not be set for non-file loggers.
        /// </summary>
        public int RotationInterval
        {
            get { return this.rotationInterval; }
            set
            {
                if (!this.HasFeature(Features.FileBacked))
                {
                    throw new InvalidLogConfigurationException("Rotation intervals are not valid for non-file loggers.");
                }
                if (value < 0)
                {
                    value = LogManager.DefaultRotate ? LogManager.DefaultRotationInterval : 0;
                }
                try
                {
                    if (value > 0)
                    {
                        LogManager.CheckRotationInterval(value);
                    }
                }
                catch (ArgumentException e)
                {
                    throw new InvalidLogConfigurationException($"Rotation interval ${value} is invalid.", e);
                }

                this.rotationInterval = value;
            }
        }

        /// <summary>
        /// Whether to provide local timestamps in filenames and log output (where applicable).
        /// </summary>
        public bool TimestampLocal { get; set; }

        /// <summary>
        /// The size of buffer to use (in megabytes) while logging.
        /// </summary>
        public int BufferSizeMB
        {
            get { return this.bufferSizeMB; }
            set
            {
                if (!LogManager.IsValidFileBufferSize(value))
                {
                    throw new InvalidLogConfigurationException("Buffer size ${value} is outside of acceptable range.");
                }

                this.bufferSizeMB = value;
            }
        }

        /// <summary>
        /// Regular expression filters for the log.
        /// </summary>
        public HashSet<string> Filters { get; }

        /// <summary>
        /// The number of sources added to the configuration.
        /// </summary>
        public int SourceCount => this.namedSources.Count + this.guidSources.Count;

        internal IEventLogger Logger
        {
            get { return this.logger; }
            set
            {
                if (value != this.logger)
                {
                    this.logger = value;
                    this.UpdateLogger();
                }
            }
        }

        /// <summary>
        /// Add a source to receive log messages from. Overwrites existing values.
        /// </summary>
        /// <param name="source">Details of the source to subscribe to.</param>
        /// <returns>True if the source does not already exist.</returns>
        public bool AddSource(LogSource source)
        {
            if (source.Name == null && source.Guid == Guid.Empty)
            {
                throw new InvalidLogConfigurationException("Provided source missing name and GUID values");
            }

            bool exists;
            if (source.Name != null)
            {
                exists = this.namedSources.ContainsKey(source.Name);
                this.namedSources[source.Name] = source;
            }
            else
            {
                exists = this.guidSources.ContainsKey(source.Guid);
                this.guidSources[source.Guid] = source;
            }

            return exists;
        }

        public static LogType StringToLogType(string type)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            switch (type.ToLower(CultureInfo.InvariantCulture))
            {
            case "con":
            case "cons":
            case "console":
                return LogType.Console;
            case "text":
            case "txt":
                return LogType.Text;
            case "etw":
            case "etl":
                return LogType.EventTracing;
            case "net":
            case "network":
                return LogType.Network;
            default:
                return LogType.None;
            }
        }

        public void AddFilter(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                throw new InvalidLogConfigurationException("empty/invalid filter value.");
            }

            if (!this.HasFeature(Features.RegexFilter))
            {
                throw new InvalidLogConfigurationException(
                    $"Log type {this.Type} does not support regular expression filters.");
            }

            filter = filter.Trim();

            if (this.Filters.Contains(filter))
            {
                throw new InvalidLogConfigurationException("duplicate filter value " + filter);
            }

            this.Filters.Add(filter);
        }

        internal bool HasFeature(Features flags)
        {
            Features caps;
            switch (this.Type)
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
                throw new InvalidOperationException("features for type " + this.Type + " are unknowable");
            }

            return ((caps & flags) != 0);
        }

        /// <summary>
        /// Update the underlying logger for an EventSource object that has been introduced within the process.
        /// </summary>
        /// <param name="eventSource">The new EventSource to apply configuration for.</param>
        internal void UpdateForEventSource(EventSource eventSource)
        {
            // We need to update our loggers any time a config shows up where they had a dependency
            // that probably wasn't resolved. This will be the case either when it was a named source
            // or it was a GUID source on a type that can't directly subscribe to GUIDs (i.e. not an ETW
            // trace session)
            LogSource levels;
            if (this.namedSources.TryGetValue(eventSource.Name, out levels) ||
                (!this.HasFeature(Features.GuidSubscription) &&
                 this.guidSources.TryGetValue(eventSource.Guid, out levels)))
            {
                if (this.HasFeature(Features.EventSourceSubscription))
                {
                    this.logger.SubscribeToEvents(eventSource, levels.Level, levels.Keywords);
                }
                else if (this.HasFeature(Features.GuidSubscription))
                {
                    this.logger.SubscribeToEvents(eventSource.Guid, levels.Level, levels.Keywords);
                }
            }
        }

        /// <summary>
        /// Apply configuration to an already-existing log destination. Expected that this is only called once per destination/.
        /// </summary>
        private void UpdateLogger()
        {
            foreach (var f in this.Filters)
            {
                this.logger.AddRegexFilter(f);
            }

            // Build a collection of all desired subscriptions so that we can subscribe in bulk at the end.
            // We do this because ordering may matter to specific types of loggers and they are best suited to
            // manage that internally.
            var subscriptions = new List<EventProviderSubscription>();
            foreach (var ns in this.namedSources)
            {
                LogManager.EventSourceInfo sourceInfo;
                string name = ns.Key;
                LogSource levels = ns.Value;
                if ((sourceInfo = LogManager.GetEventSourceInfo(name)) != null)
                {
                    subscriptions.Add(new EventProviderSubscription(sourceInfo.Source)
                                      {
                                          MinimumLevel = levels.Level,
                                          Keywords = levels.Keywords
                                      });
                }
            }

            foreach (var gs in this.guidSources)
            {
                LogManager.EventSourceInfo sourceInfo;
                var guid = gs.Key;
                var levels = gs.Value;
                if (this.HasFeature(Features.GuidSubscription))
                {
                    subscriptions.Add(new EventProviderSubscription(guid)
                                      {
                                          MinimumLevel = levels.Level,
                                          Keywords = levels.Keywords
                                      });
                }
                else if (this.HasFeature(Features.EventSourceSubscription) &&
                         (sourceInfo = LogManager.GetEventSourceInfo(guid)) != null)
                {
                    subscriptions.Add(new EventProviderSubscription(sourceInfo.Source)
                                      {
                                          MinimumLevel = levels.Level,
                                          Keywords = levels.Keywords
                                      });
                }
            }

            this.logger.SubscribeToEvents(subscriptions);
        }

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
    }
}