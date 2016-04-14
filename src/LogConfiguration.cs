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
    using System.Linq;

    using Newtonsoft.Json;

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
    [JsonObject(MemberSerialization = MemberSerialization.OptIn), JsonConverter(typeof(Converter))]
    public sealed class LogConfiguration
    {
        private readonly HashSet<EventProviderSubscription> subscriptions = new HashSet<EventProviderSubscription>();

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
                throw new InvalidConfigurationException($"Log type {logType} is invalid.");
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
                    throw new InvalidConfigurationException("Log name must be specified.");
                }
                break;
            }
            if (this.HasFeature(Features.FileBacked) && name.IndexOfAny(Path.GetInvalidFileNameChars()) != -1)
            {
                throw new InvalidConfigurationException($"base name {name} of log is invalid");
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
        public LogType Type { get; internal set; }

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
                    throw new InvalidConfigurationException("Directories are not valid for non-file loggers.");
                }
                try
                {
                    this.directory = LogManager.GetQualifiedDirectory(value);
                }
                catch (ArgumentException e)
                {
                    throw new InvalidConfigurationException($"Directory {value} is not valid.", e);
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
                    throw new InvalidConfigurationException("Filename templates are not valid for non-file loggers.");
                }
                if (!FileBackedLogger.IsValidFilenameTemplate(value))
                {
                    throw new InvalidConfigurationException($"Filename template '{value}' is invalid.");
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
                    throw new InvalidConfigurationException("Hostnames are not valid for non-network loggers.");
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
                    throw new InvalidConfigurationException("Ports are not valid for non-network loggers.");
                }
                if (value == 0)
                {
                    throw new InvalidConfigurationException($"Port {value} is invalid.");
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
                    throw new InvalidConfigurationException("Rotation intervals are not valid for non-file loggers.");
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
                    throw new InvalidConfigurationException($"Rotation interval {value} is invalid.", e);
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
                    throw new InvalidConfigurationException($"Buffer size {value} is outside of acceptable range.");
                }

                this.bufferSizeMB = value;
            }
        }

        /// <summary>
        /// Regular expression filters for the log.
        /// </summary>
        public HashSet<string> Filters { get; }

        /// <summary>
        /// The number of subscriptions added to the configuration.
        /// </summary>
        public int SubscriptionCount => this.subscriptions.Count;

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

        public override bool Equals(object obj)
        {
            var other = obj as LogConfiguration;
            return other != null && this.Equals(other);
        }

        private bool Equals(LogConfiguration other)
        {
            return string.Equals(this.Name, other.Name, StringComparison.OrdinalIgnoreCase) && this.Type == other.Type;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((this.Name != null ? StringComparer.OrdinalIgnoreCase.GetHashCode(this.Name) : 0) * 397) ^
                       (int)this.Type;
            }
        }

        /// <summary>
        /// Add a subscription to receive log messages from. Combines with existing values and takes the lowest 
        /// <see cref="EventProviderSubscription.MinimumLevel" /> and the full set of
        /// <see cref="EventProviderSubscription.Keywords" />.
        /// </summary>
        /// <param name="subscription">Details of the subscription to add.</param>
        /// <returns>True if the subscription does not already exist.</returns>
        public bool AddSubscription(EventProviderSubscription subscription)
        {
            if (subscription.Name == null && subscription.ProviderID == Guid.Empty)
            {
                throw new InvalidConfigurationException("Provided subscription missing name and GUID values");
            }

            var newSubscription = true;
            if (this.subscriptions.Contains(subscription))
            {
                newSubscription = false;
                var currentSubscription = this.subscriptions.First(s => s.Equals(subscription));
                currentSubscription.MinimumLevel =
                    (EventLevel)Math.Min((int)currentSubscription.MinimumLevel, (int)subscription.MinimumLevel);
                currentSubscription.Keywords |= subscription.Keywords;
                subscription = currentSubscription;
            }
            else
            {
                this.subscriptions.Add(subscription);
            }
            if (subscription.IsResolved)
            {
                this.Logger?.SubscribeToEvents(subscription);
            }

            return newSubscription;
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
                throw new InvalidConfigurationException("empty/invalid filter value.");
            }

            if (!this.HasFeature(Features.RegexFilter))
            {
                throw new InvalidConfigurationException(
                    $"Log type {this.Type} does not support regular expression filters.");
            }

            filter = filter.Trim();

            if (this.Filters.Contains(filter))
            {
                throw new InvalidConfigurationException("duplicate filter value " + filter);
            }

            this.Filters.Add(filter);
            this.Logger?.AddRegexFilter(filter);
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
            // that probably wasn't resolved. This will be the case either when it was a named subscription
            // or it was a GUID subscription on a type that can't directly subscribe to GUIDs (i.e. not an ETW
            // trace session)
            var subscription =
                this.subscriptions.FirstOrDefault(
                                                  s =>
                                                  string.Equals(s.Name, eventSource.Name,
                                                                StringComparison.OrdinalIgnoreCase) ||
                                                  (!this.HasFeature(Features.GuidSubscription) &&
                                                   s.ProviderID == eventSource.Guid));
            if (subscription != null && !subscription.IsResolved)
            {
                subscription.UpdateSource(eventSource);
                if (this.HasFeature(Features.EventSourceSubscription))
                {
                    this.logger.SubscribeToEvents(eventSource, subscription.MinimumLevel, subscription.Keywords);
                }
                else if (this.HasFeature(Features.GuidSubscription))
                {
                    this.logger.SubscribeToEvents(eventSource.Guid, subscription.MinimumLevel, subscription.Keywords);
                }
            }
        }

        internal void Merge(LogConfiguration otherLog)
        {
            foreach (var sub in otherLog.subscriptions)
            {
                this.AddSubscription(sub);
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
            var supportedSubscriptions = new List<EventProviderSubscription>();
            foreach (var sub in this.subscriptions)
            {
                if (!sub.IsResolved)
                {
                    continue;
                }
                if (sub.Source != null || (this.HasFeature(Features.GuidSubscription) && sub.ProviderID != Guid.Empty))
                {
                    supportedSubscriptions.Add(sub);
                }
            }

            this.logger.SubscribeToEvents(supportedSubscriptions);
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

        private sealed class Converter : JsonConverter
        {
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                throw new NotImplementedException();
                var log = value as LogConfiguration;
                if (log == null)
                {
                    throw new ArgumentNullException(nameof(value));
                }

                writer.WriteStartObject();
                writer.WriteEndObject();
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue,
                                            JsonSerializer serializer)
            {
                throw new NotImplementedException();
            }

            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(LogConfiguration);
            }
        }
    }
}