// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// Several generator plugins write timestamped output files to the *current working
// directory*. The generator tests temporarily redirect the process CWD to a temp folder,
// which is process-global state — so the whole assembly must run serially to prevent a
// concurrent test from observing or polluting the redirected directory.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
