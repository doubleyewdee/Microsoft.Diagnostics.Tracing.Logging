# v1.0.0

**NOTE**: Changes here are relative to the previously Microsoft-internal codebase this package
came from.

* Namespace changed from 'Microsoft.Bing.Logging' to 'Microsoft.Diagnostics.Tracing.Logging' to be
  in step with the Microsoft.Diagnostics.Tracing package.
* Configuration may now be (preferably!) provided as JSON.
* Configuring a negative rotation interval is no longer an error and simply indicates "use the default."

