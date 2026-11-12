// Emitted explicitly (not just via the MSBuild property) so that CsWin32's
// build-task generation pass also sees it: its source-generated COM output
// ([GeneratedComInterface]) requires runtime marshalling to be disabled.
[assembly: System.Runtime.CompilerServices.DisableRuntimeMarshalling]
