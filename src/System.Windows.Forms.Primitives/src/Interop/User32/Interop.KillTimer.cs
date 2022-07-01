﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.InteropServices;

internal static partial class Interop
{
    internal static partial class User32
    {
        [LibraryImport(Libraries.User32)]
        public static partial BOOL KillTimer(IntPtr hWnd, IntPtr uIDEvent);

        public static BOOL KillTimer(HandleRef hWnd, IntPtr uIDEvent)
        {
            BOOL result = KillTimer(hWnd.Handle, uIDEvent);
            GC.KeepAlive(hWnd.Wrapper);
            return result;
        }
    }
}
