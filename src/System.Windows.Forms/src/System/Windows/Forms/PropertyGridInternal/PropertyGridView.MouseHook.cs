﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static Interop;

namespace System.Windows.Forms.PropertyGridInternal
{
    internal partial class PropertyGridView
    {
        internal class MouseHook
        {
            private readonly PropertyGridView gridView;
            private readonly Control control;
            private readonly IMouseHookClient client;

            internal uint _thisProcessID;
            private GCHandle _mouseHookRoot;
            private IntPtr _mouseHookHandle = IntPtr.Zero;
            private bool hookDisable;

            private bool processing;

            public MouseHook(Control control, IMouseHookClient client, PropertyGridView gridView)
            {
                this.control = control;
                this.gridView = gridView;
                this.client = client;
#if DEBUG
                callingStack = Environment.StackTrace;
#endif
            }

#if DEBUG
            readonly string callingStack;
            ~MouseHook()
            {
                Debug.Assert(_mouseHookHandle == IntPtr.Zero, "Finalizing an active mouse hook.  This will crash the process.  Calling stack: " + callingStack);
            }
#endif

            public bool DisableMouseHook
            {
                set
                {
                    hookDisable = value;
                    if (value)
                    {
                        UnhookMouse();
                    }
                }
            }

            public virtual bool HookMouseDown
            {
                get
                {
                    GC.KeepAlive(this);
                    return _mouseHookHandle != IntPtr.Zero;
                }
                set
                {
                    if (value && !hookDisable)
                    {
                        HookMouse();
                    }
                    else
                    {
                        UnhookMouse();
                    }
                }
            }

            public void Dispose()
            {
                UnhookMouse();
            }

            /// <summary>
            ///  Sets up the needed windows hooks to catch messages.
            /// </summary>
            private void HookMouse()
            {
                GC.KeepAlive(this);
                // Locking 'this' here is ok since this is an internal class.
                lock (this)
                {
                    if (_mouseHookHandle != IntPtr.Zero)
                    {
                        return;
                    }

                    if (_thisProcessID == 0)
                    {
                        User32.GetWindowThreadProcessId(control, out _thisProcessID);
                    }

                    var hook = new User32.HOOKPROC(new MouseHookObject(this).Callback);
                    _mouseHookRoot = GCHandle.Alloc(hook);
                    _mouseHookHandle = User32.SetWindowsHookExW(
                        User32.WH.MOUSE,
                        hook,
                        IntPtr.Zero,
                        Kernel32.GetCurrentThreadId());
                    Debug.Assert(_mouseHookHandle != IntPtr.Zero, "Failed to install mouse hook");
                    Debug.WriteLineIf(CompModSwitches.DebugGridView.TraceVerbose, "DropDownHolder:HookMouse()");
                }
            }

            /// <summary>
            ///  HookProc used for catch mouse messages.
            /// </summary>
            private unsafe IntPtr MouseHookProc(User32.HC nCode, IntPtr wparam, IntPtr lparam)
            {
                GC.KeepAlive(this);
                if (nCode == User32.HC.ACTION)
                {
                    User32.MOUSEHOOKSTRUCT* mhs = (User32.MOUSEHOOKSTRUCT*)lparam;
                    if (mhs is not null)
                    {
                        switch (unchecked((User32.WM)(long)wparam))
                        {
                            case User32.WM.LBUTTONDOWN:
                            case User32.WM.MBUTTONDOWN:
                            case User32.WM.RBUTTONDOWN:
                            case User32.WM.NCLBUTTONDOWN:
                            case User32.WM.NCMBUTTONDOWN:
                            case User32.WM.NCRBUTTONDOWN:
                            case User32.WM.MOUSEACTIVATE:
                                if (ProcessMouseDown(mhs->hWnd, mhs->pt.X, mhs->pt.Y))
                                {
                                    return (IntPtr)1;
                                }
                                break;
                        }
                    }
                }

                return User32.CallNextHookEx(new HandleRef(this, _mouseHookHandle), nCode, wparam, lparam);
            }

            /// <summary>
            ///  Removes the windowshook that was installed.
            /// </summary>
            private void UnhookMouse()
            {
                GC.KeepAlive(this);
                // Locking 'this' here is ok since this is an internal class.
                lock (this)
                {
                    if (_mouseHookHandle != IntPtr.Zero)
                    {
                        User32.UnhookWindowsHookEx(new HandleRef(this, _mouseHookHandle));
                        _mouseHookRoot.Free();
                        _mouseHookHandle = IntPtr.Zero;
                        Debug.WriteLineIf(CompModSwitches.DebugGridView.TraceVerbose, "DropDownHolder:UnhookMouse()");
                    }
                }
            }

            /*
           * Here is where we force validation on any clicks outside the
           */
            private bool ProcessMouseDown(IntPtr hWnd, int x, int y)
            {
                // If we put up the "invalid" message box, it appears this
                // method is getting called re-entrantly when it shouldn't be.
                // this prevents us from recursing.
                if (processing)
                {
                    return false;
                }

                IntPtr hWndAtPoint = hWnd;
                IntPtr handle = control.Handle;
                Control ctrlAtPoint = FromHandle(hWndAtPoint);

                // if it's us or one of our children, just process as normal
                if (hWndAtPoint != handle && !control.Contains(ctrlAtPoint))
                {
                    Debug.Assert(_thisProcessID != 0, "Didn't get our process id!");

                    // Make sure the window is in our process
                    User32.GetWindowThreadProcessId(hWndAtPoint, out uint pid);

                    // if this isn't our process, unhook the mouse.
                    if (pid != _thisProcessID)
                    {
                        HookMouseDown = false;
                        return false;
                    }

                    bool needCommit = false;

                    // if this a sibling control (e.g. the drop down or buttons), just forward the message and skip the commit
                    needCommit = ctrlAtPoint is null ? true : !gridView.IsSiblingControl(control, ctrlAtPoint);

                    try
                    {
                        processing = true;

                        if (needCommit)
                        {
                            if (client.OnClickHooked())
                            {
                                return true; // there was an error, so eat the mouse
                            }
                        }
                    }
                    finally
                    {
                        processing = false;
                    }

                    // cancel our hook at this point
                    HookMouseDown = false;
                    //gridView.UnfocusSelection();
                }
                return false;
            }

            /// <summary>
            ///  Forwards messageHook calls to ToolTip.messageHookProc
            /// </summary>
            private class MouseHookObject
            {
                internal WeakReference reference;

                public MouseHookObject(MouseHook parent)
                {
                    reference = new WeakReference(parent, false);
                }

                public virtual IntPtr Callback(User32.HC nCode, IntPtr wparam, IntPtr lparam)
                {
                    IntPtr ret = IntPtr.Zero;
                    try
                    {
                        MouseHook control = (MouseHook)reference.Target;
                        if (control is not null)
                        {
                            ret = control.MouseHookProc(nCode, wparam, lparam);
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                    return ret;
                }
            }
        }
    }
}
