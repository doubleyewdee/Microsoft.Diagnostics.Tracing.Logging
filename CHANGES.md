# v1.0.0

**NOTE**: Changes here are relative to the previously Microsoft-internal codebase this package
came from.

* Namespace changed from 'Microsoft.Bing.Logging' to 'Microsoft.Diagnostics.Tracing.Logging' to be
  in step with the Microsoft.Diagnostics.Tracing package.
* Log retention polices have been added (see configuration documentation for usage restrictions)
* Hostname and "milliseconds from midnight" formats must not have format specifiers.  