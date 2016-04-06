# v1.0.0

**NOTE**: Changes here are relative to the previously Microsoft-internal codebase this package
came from.

* Namespace changed from 'Microsoft.Bing.Logging' to 'Microsoft.Diagnostics.Tracing.Logging' to be
  in step with the Microsoft.Diagnostics.Tracing package.
* Configuration may now be (preferably!) provided as JSON.
* Configuring a negative rotation interval is no longer an error and simply indicates "use the default."
* Several functions were obsoleted with preferable replacements, particularly in terms of using
  the manager to create/find/close loggers.
  * Basically creating a logger is now done using the new `LogConfiguration` type which can be used
    to specify all relevant details of a log.
* Some public members in public types converted to properties for potential future use.

