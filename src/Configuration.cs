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
    using System.Diagnostics.CodeAnalysis;
    using System.Diagnostics.Tracing;
    using System.Globalization;
    using System.IO;
    using System.Threading;
    using System.Xml;

    public sealed partial class LogManager
    {
        #region Public
        /// <summary>
        /// Provide string-based configuration which will be applied additively after any file configuration.
        /// </summary>
        /// <remarks>
        /// Any change will force a full configuration reload. This function is not thread-safe with regards to
        /// concurrent callers.
        /// </remarks>
        /// <param name="configurationXml">A string containing the XML configuration</param>
        /// <returns>True if the configuration was successfully applied</returns>
        public static bool SetConfiguration(string configurationXml)
        {
            if (!IsConfigurationValid(configurationXml))
            {
                return false;
            }

            singleton.configurationData = configurationXml;
            return singleton.ApplyConfiguration();
        }

        /// <summary>
        /// Check if a configuration string is valid
        /// </summary>
        /// <param name="configurationXml">A string containing the XML configuration</param>
        /// <returns>true if the configuration is valid, false otherwise</returns>
        public static bool IsConfigurationValid(string configurationXml)
        {
            var unused = new Dictionary<string, LogConfiguration>(StringComparer.OrdinalIgnoreCase);
            return ParseConfiguration(configurationXml, unused);
        }

        /// <summary>
        /// Assign a file to read configuration from
        /// </summary>
        /// <param name="filename">The file to read configuration from (or null to remove use of the file)</param>
        /// <returns>true if the file was valid, false otherwise</returns>
        public static bool SetConfigurationFile(string filename)
        {
            return singleton.UpdateConfigurationFile(filename);
        }
        #endregion

        #region Private
        // HEY! HEY YOU! Are you adding stuff here? You're adding stuff, it's cool. Just go update
        // the 'configuration.md' file in doc with what you've added. Santa will bring you bonus gifts.
        private const string EtwOverrideXpath = "/loggers/etwlogging";
        private const string EtwOverrideEnabledAttribute = "enabled";
        private const string LogTagXpath = "/loggers/log";
        private const string LogBufferSizeAttribute = "buffersizemb";
        private const string LogDirectoryAttribute = "directory";
        private const string LogFilenameTemplateAttribute = "filenametemplate";
        private const string LogTimestampLocal = "timestamplocal";
        private const string LogFilterTag = "filter";
        private const string LogNameAttribute = "name";
        private const string LogRotationAttribute = "rotationinterval";
        private const string LogTypeAttribute = "type";
        private const string LogHostnameAttribute = "hostname";
        private const string LogPortAttribute = "port";
        private const string SourceTag = "source";
        private const string SourceKeywordsAttribute = "keywords";
        private const string SourceMinSeverityAttribute = "minimumseverity";
        private const string SourceProviderIDAttribute = "providerid";
        private const string SourceProviderNameAttribute = "name";
        private string configurationFile;
        private long configurationFileLastWrite;
        private FileSystemWatcher configurationFileWatcher;
        internal int configurationFileReloadCount; // primarily a test hook.
        private string configurationFileData;
        private string configurationData;
        private Dictionary<string, LogConfiguration> logConfigurations;

        private static bool ParseConfiguration(string configurationXml, Dictionary<string, LogConfiguration> loggers)
        {
            bool clean = true; // used to track whether any errors were encountered

            if (string.IsNullOrEmpty(configurationXml))
            {
                return true; // it's okay to have nothing at all
            }

            var configuration = new XmlDocument();
            try
            {
                configuration.LoadXml(configurationXml);
            }
            catch (XmlException)
            {
                InternalLogger.Write.InvalidConfiguration("Configuration was not valid XML");
                return false;
            }

            XmlNode node = configuration.SelectSingleNode(EtwOverrideXpath);
            if (node != null)
            {
                XmlNode setting = node.Attributes.GetNamedItem(EtwOverrideEnabledAttribute);
                bool isEnabled = (AllowEtwLogging == AllowEtwLoggingValues.Enabled);
                if (setting == null || !bool.TryParse(setting.Value, out isEnabled))
                {
                    InternalLogger.Write.InvalidConfiguration(EtwOverrideXpath + " tag has invalid " +
                                                              EtwOverrideEnabledAttribute + " attribute");
                    clean = false;
                }

                AllowEtwLogging = isEnabled ? AllowEtwLoggingValues.Enabled : AllowEtwLoggingValues.Disabled;
            }

            foreach (XmlNode log in configuration.SelectNodes(LogTagXpath))
            {
                string name = null;
                LogType type;
                if (log.Attributes[LogNameAttribute] != null)
                {
                    name = log.Attributes[LogNameAttribute].Value.Trim();
                }

                // If no type is provided we currently default to text.
                if (log.Attributes[LogTypeAttribute] == null)
                {
                    type = LogType.Text;
                }
                else
                {
                    type = LogConfiguration.StringToLogType(log.Attributes[LogTypeAttribute].Value);
                }

                if (type == LogType.None)
                {
                    InternalLogger.Write.InvalidConfiguration("invalid log type " + log.Attributes[LogTypeAttribute].Value);
                    clean = false;
                    continue;
                }

                if (type == LogType.Console)
                {
                    if (name != null)
                    {
                        InternalLogger.Write.InvalidConfiguration("console log should not have a name");
                        clean = false;
                    }
                }
                else
                {
                    // XXX: MOVE ME
                    if (type == LogType.EventTracing && AllowEtwLogging == AllowEtwLoggingValues.Disabled)
                    {
                        InternalLogger.Write.OverridingEtwLogging(name);
                        type = LogType.Text;
                    }
                }

                // If a log is listed in duplicates we will discard the previous data entirely. This is a change from historic
                // (pre OSS-release) behavior which was... quasi-intentional shall we say. The author is unaware of anybody
                // using this capability and, since it confusing at best, would like for it to go away.
                LogConfiguration config;
                try
                {
                    config = new LogConfiguration(name, type);

                    clean &= ParseLogNode(log, config);

                    if (config.SourceCount == 0)
                    {
                        InternalLogger.Write.InvalidConfiguration($"log destination {config.Name} has no sources.");
                        clean = false;
                        continue;
                    }

                    loggers[config.Name] = config;
                }
                catch (InvalidLogConfigurationException e)
                {
                    InternalLogger.Write.InvalidConfiguration(e.Message);
                    clean = false;
                    continue;
                }
            }

            return clean;
        }

        private bool ApplyConfiguration()
        {
            var newConfig = new Dictionary<string, LogConfiguration>(StringComparer.OrdinalIgnoreCase);
            if (!ParseConfiguration(this.configurationFileData, newConfig)
                || !ParseConfiguration(this.configurationData, newConfig))
            {
                return false;
            }

            lock (this.loggersLock)
            {
                foreach (var logger in this.fileLoggers.Values)
                {
                    logger.Dispose();
                }
                foreach (var logger in this.networkLoggers.Values)
                {
                    logger.Dispose();
                }
                this.fileLoggers.Clear();
                this.networkLoggers.Clear();
                this.logConfigurations = newConfig;

                foreach (var kvp in this.logConfigurations)
                {
                    var logName = kvp.Key;
                    var logConfig = kvp.Value;
                    CreateLogger(logConfig);
                }
            }

            return true;
        }

        private static bool ParseLogNode(XmlNode xmlNode, LogConfiguration config)
        {
            var clean = true;
            foreach (XmlAttribute logAttribute in xmlNode.Attributes)
            {
                try
                {
                    switch (logAttribute.Name.ToLower(CultureInfo.InvariantCulture))
                    {
                    case LogBufferSizeAttribute:
                        config.BufferSizeMB = int.Parse(logAttribute.Value);
                        break;
                    case LogDirectoryAttribute:
                        config.Directory = logAttribute.Value;
                        break;
                    case LogFilenameTemplateAttribute:
                        config.FilenameTemplate = logAttribute.Value;
                        break;
                    case LogTimestampLocal:
                        config.TimestampLocal = bool.Parse(logAttribute.Value);
                        break;
                    case LogRotationAttribute:
                        config.RotationInterval = int.Parse(logAttribute.Value);
                        break;
                    case LogHostnameAttribute:
                        config.Hostname = logAttribute.Value;
                        break;
                    case LogPortAttribute:
                        config.Port = ushort.Parse(logAttribute.Value);
                        break;
                    }
                }
                catch (Exception e) when (e is FormatException || e is OverflowException)
                {
                    throw new InvalidLogConfigurationException(
                        $"Attribute ${logAttribute.Name} has invalid value ${logAttribute.Value}",
                        e);
                }
            }

            clean &= ParseLogSources(xmlNode, config);
            clean &= ParseLogFilters(xmlNode, config);
            return clean;
        }

        private static bool ParseLogSources(XmlNode xmlNode, LogConfiguration config)
        {
            bool clean = true;

            foreach (XmlNode source in xmlNode.SelectNodes(SourceTag))
            {
                string sourceName = null;
                Guid sourceProvider = Guid.Empty;
                var level = EventLevel.Informational;
                var keywords = (long)EventKeywords.None;
                foreach (XmlAttribute sourceAttribute in source.Attributes)
                {
                    switch (sourceAttribute.Name.ToLower(CultureInfo.InvariantCulture))
                    {
                    case SourceKeywordsAttribute:
                        // Yes, really. The .NET integer TryParse methods will get PISSED if they see 0x in front of
                        // hex digits. Dumb hack is dumb.
                        string value = sourceAttribute.Value.Trim();
                        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        {
                            value = value.Substring(2);
                        }

                        if (!long.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                                           out keywords))
                        {
                            InternalLogger.Write.InvalidConfiguration("invalid keywords value " + sourceAttribute.Value);
                            clean = false;
                        }
                        break;
                    case SourceMinSeverityAttribute:
                        if (!Enum.TryParse(sourceAttribute.Value, true, out level))
                        {
                            InternalLogger.Write.InvalidConfiguration("invalid severity value " + sourceAttribute.Value);
                            clean = false;
                        }
                        break;
                    case SourceProviderIDAttribute:
                        if (!Guid.TryParse(sourceAttribute.Value, out sourceProvider))
                        {
                            InternalLogger.Write.InvalidConfiguration("invalid providerID GUID " + sourceAttribute.Value);
                            clean = false;
                        }
                        break;
                    case SourceProviderNameAttribute:
                        sourceName = sourceAttribute.Value.Trim();
                        break;
                    }
                }

                if (sourceProvider != Guid.Empty)
                {
                    config.AddSource(new LogSource(sourceProvider, level, (EventKeywords)keywords));
                }
                else if (!string.IsNullOrEmpty(sourceName))
                {
                    config.AddSource(new LogSource(sourceName, level, (EventKeywords)keywords));
                }
                else
                {
                    InternalLogger.Write.InvalidConfiguration("source has neither name nor guid");
                    clean = false;
                }
            }

            return clean;
        }

        private static bool ParseLogFilters(XmlNode xmlNode, LogConfiguration config)
        {
            bool clean = true;

            foreach (XmlNode source in xmlNode.SelectNodes(LogFilterTag))
            {
                try
                {
                    config.AddFilter(source.InnerText);
                }
                catch (InvalidLogConfigurationException e)
                {
                    InternalLogger.Write.InvalidConfiguration(e.Message);
                    clean = false;
                }
            }

            return clean;
        }

        [SuppressMessage("Microsoft.Security", "CA2122:DoNotIndirectlyExposeMethodsWithLinkDemands")]
        private bool UpdateConfigurationFile(string filename)
        {
            if (filename != null && !File.Exists(filename))
            {
                throw new FileNotFoundException("configuration file does not exist", filename);
            }

            if (this.configurationFileWatcher != null)
            {
                this.configurationFileWatcher.Dispose();
                this.configurationFileWatcher = null;
            }

            InternalLogger.Write.SetConfigurationFile(filename);
            if (filename != null)
            {
                this.configurationFile = Path.GetFullPath(filename);
                this.configurationFileWatcher = new FileSystemWatcher();
                this.configurationFileWatcher.Path = Path.GetDirectoryName(this.configurationFile);
                this.configurationFileWatcher.Filter = Path.GetFileName(this.configurationFile);
                this.configurationFileWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size |
                                                             NotifyFilters.CreationTime;
                this.configurationFileWatcher.Changed += this.OnConfigurationFileChanged;
                this.configurationFileWatcher.EnableRaisingEvents = true;
                return ReadConfigurationFile(this.configurationFile);
            }

            singleton.configurationFileData = null;
            return singleton.ApplyConfiguration();
        }

        private static bool ReadConfigurationFile(string filename)
        {
            Stream file = null;
            bool success = false;
            try
            {
                file = new FileStream(filename, FileMode.Open, FileAccess.Read);
                using (var reader = new StreamReader(file))
                {
                    file = null;
                    singleton.configurationFileData = reader.ReadToEnd();
                    success = singleton.ApplyConfiguration();
                }
            }
            catch (IOException e)
            {
                InternalLogger.Write.InvalidConfiguration(string.Format("Could not open configuration: {0} {1}",
                                                                        e.GetType(), e.Message));
            }
            finally
            {
                if (file != null)
                {
                    file.Dispose();
                }
            }

            if (success)
            {
                InternalLogger.Write.ProcessedConfigurationFile(filename);
                ++singleton.configurationFileReloadCount;
            }
            else
            {
                InternalLogger.Write.InvalidConfigurationFile(filename);
            }
            return success;
        }

        private void OnConfigurationFileChanged(object source, FileSystemEventArgs e)
        {
            long writeTime = new FileInfo(e.FullPath).LastWriteTimeUtc.ToFileTimeUtc();

            if (writeTime !=
                Interlocked.CompareExchange(ref this.configurationFileLastWrite, writeTime,
                                            this.configurationFileLastWrite))
            {
                ReadConfigurationFile(e.FullPath);
            }
        }
        #endregion
    }
}