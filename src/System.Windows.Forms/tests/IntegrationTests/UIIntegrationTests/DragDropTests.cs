﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Drawing;
using Xunit;
using Xunit.Abstractions;

namespace System.Windows.Forms.UITests;

public class DragDropTests : ControlTestBase
{
    public const int DragDropDelayMS = 100;

    public DragDropTests(ITestOutputHelper testOutputHelper)
        : base(testOutputHelper)
    {
    }

    [WinFormsFact]
    public async Task DragDrop_QueryDefaultCursors_Async()
    {
        await RunFormWithoutControlAsync(() => new DragDropForm(TestOutputHelper), async (form) =>
        {
            await MoveMouseToControlAsync(form.ListDragSource);

            await InputSimulator.SendAsync(
                form,
                inputSimulator => inputSimulator.Mouse.LeftButtonDown());

            var targetMousePosition = ToVirtualPoint(form.ListDragTarget.PointToScreen(new Point(20, 20)));
            await InputSimulator.SendAsync(
                form,
                inputSimulator => inputSimulator.Mouse
                    .LeftButtonDown()
                    .Sleep(100)
                    .MoveMouseTo(targetMousePosition.X - 40, targetMousePosition.Y)
                    .Sleep(DragDropDelayMS)
                    .MoveMouseTo(targetMousePosition.X, targetMousePosition.Y)
                    .Sleep(DragDropDelayMS) // slight delay so drag&drop triggered
                    .MoveMouseTo(targetMousePosition.X + 2, targetMousePosition.Y + 2)
                    .Sleep(DragDropDelayMS) // slight delay so drag&drop triggered
                    .MoveMouseTo(targetMousePosition.X + 4, targetMousePosition.Y + 4)
                    .Sleep(DragDropDelayMS)
                    .LeftButtonUp()
                    .Sleep(DragDropDelayMS));

            Assert.Equal(1, form.ListDragTarget.Items.Count);
        });
    }

    class DragDropForm : Form
    {
        public ListBox ListDragSource;
        public ListBox ListDragTarget;
        private CheckBox UseCustomCursorsCheck;
        private Label DropLocationLabel;

        private int indexOfItemUnderMouseToDrag;
        private int indexOfItemUnderMouseToDrop;

        private Rectangle dragBoxFromMouseDown;
        private Point screenOffset;

        private Cursor? MyNoDropCursor;
        private Cursor? MyNormalCursor;
        private readonly ITestOutputHelper testOutputHelper;

        public DragDropForm(ITestOutputHelper testOutputHelper)
        {
            this.ListDragSource = new ListBox();
            this.ListDragTarget = new ListBox();
            this.UseCustomCursorsCheck = new CheckBox();
            this.DropLocationLabel = new Label();

            this.SuspendLayout();

            // ListDragSource
            this.ListDragSource.Items.AddRange(new object[]
            {
                "one", "two", "three", "four",
                "five", "six", "seven", "eight",
                "nine", "ten"
            });
            this.ListDragSource.Location = new Point(10, 17);
            this.ListDragSource.Size = new Size(120, 225);
            this.ListDragSource.MouseDown += this.ListDragSource_MouseDown;
            this.ListDragSource.QueryContinueDrag += this.ListDragSource_QueryContinueDrag;
            this.ListDragSource.MouseUp += this.ListDragSource_MouseUp;
            this.ListDragSource.MouseMove += this.ListDragSource_MouseMove;
            this.ListDragSource.GiveFeedback += this.ListDragSource_GiveFeedback;

            // ListDragTarget
            this.ListDragTarget.AllowDrop = true;
            this.ListDragTarget.Location = new Point(154, 17);
            this.ListDragTarget.Size = new Size(120, 225);
            this.ListDragTarget.DragOver += this.ListDragTarget_DragOver;
            this.ListDragTarget.DragDrop += this.ListDragTarget_DragDrop;
            this.ListDragTarget.DragEnter += this.ListDragTarget_DragEnter;
            this.ListDragTarget.DragLeave += this.ListDragTarget_DragLeave;

            // UseCustomCursorsCheck
            this.UseCustomCursorsCheck.Location = new Point(10, 243);
            this.UseCustomCursorsCheck.Size = new Size(137, 24);
            this.UseCustomCursorsCheck.Text = "Use Custom Cursors";

            // DropLocationLabel
            this.DropLocationLabel.Location = new Point(154, 245);
            this.DropLocationLabel.Size = new Size(137, 24);
            this.DropLocationLabel.Text = "None";

            // Form1
            this.ClientSize = new Size(292, 270);
            this.Controls.AddRange(new Control[]
            {
                this.ListDragSource,
                this.ListDragTarget,
                this.UseCustomCursorsCheck,
                this.DropLocationLabel
            });
            this.testOutputHelper = testOutputHelper;
        }

        private void ListDragSource_MouseDown(object? sender, MouseEventArgs e)
        {
            // Get the index of the item the mouse is below.
            indexOfItemUnderMouseToDrag = ListDragSource.IndexFromPoint(e.X, e.Y);
            testOutputHelper.WriteLine($"Mouse down on drag source at position ({e.X},{e.Y}). Index of element under mouse: {indexOfItemUnderMouseToDrag}");

            if (indexOfItemUnderMouseToDrag != ListBox.NoMatches)
            {
                // Remember the point where the mouse down occurred. The DragSize indicates
                // the size that the mouse can move before a drag event should be started.
                Size dragSize = SystemInformation.DragSize;

                // Create a rectangle using the DragSize, with the mouse position being
                // at the center of the rectangle.
                dragBoxFromMouseDown = new Rectangle(
                    new Point(e.X - (dragSize.Width / 2),
                              e.Y - (dragSize.Height / 2)),
                    dragSize);
            }
            else
            {
                // Reset the rectangle if the mouse is not over an item in the ListBox.
                dragBoxFromMouseDown = Rectangle.Empty;
            }
        }

        private void ListDragSource_MouseUp(object? sender, MouseEventArgs e)
        {
            // Reset the drag rectangle when the mouse button is raised.
            dragBoxFromMouseDown = Rectangle.Empty;
            testOutputHelper.WriteLine($"Mouse up on drag source at position ({e.X},{e.Y}).");
        }

        private void ListDragSource_MouseMove(object? sender, MouseEventArgs e)
        {
            testOutputHelper.WriteLine($"Mouse move on drag source to position ({e.X},{e.Y}) with buttons {e.Button}.");
            if ((e.Button & MouseButtons.Left) == MouseButtons.Left)
            {
                // If the mouse moves outside the rectangle, start the drag.
                if (dragBoxFromMouseDown != Rectangle.Empty &&
                    !dragBoxFromMouseDown.Contains(e.X, e.Y))
                {
                    // Create custom cursors for the drag-and-drop operation.
                    try
                    {
                        MyNormalCursor = new Cursor("3dwarro.cur");
                        MyNoDropCursor = new Cursor("3dwno.cur");
                    }
                    catch
                    {
                        // An error occurred while attempting to load the cursors, so use
                        // standard cursors.
                        UseCustomCursorsCheck.Checked = false;
                    }
                    finally
                    {
                        // The screenOffset is used to account for any desktop bands
                        // that may be at the top or left side of the screen when
                        // determining when to cancel the drag drop operation.
                        screenOffset = SystemInformation.WorkingArea.Location;

                        // Proceed with the drag-and-drop, passing in the list item.
                        DragDropEffects dropEffect = ListDragSource.DoDragDrop(ListDragSource.Items[indexOfItemUnderMouseToDrag], DragDropEffects.All | DragDropEffects.Link);

                        // If the drag operation was a move then remove the item.
                        if (dropEffect == DragDropEffects.Move)
                        {
                            ListDragSource.Items.RemoveAt(indexOfItemUnderMouseToDrag);

                            // Selects the previous item in the list as long as the list has an item.
                            if (indexOfItemUnderMouseToDrag > 0)
                                ListDragSource.SelectedIndex = indexOfItemUnderMouseToDrag - 1;

                            else if (ListDragSource.Items.Count > 0)
                                // Selects the first item.
                                ListDragSource.SelectedIndex = 0;
                        }

                        // Dispose of the cursors since they are no longer needed.
                        if (MyNormalCursor != null)
                            MyNormalCursor.Dispose();

                        if (MyNoDropCursor != null)
                            MyNoDropCursor.Dispose();
                    }
                }
            }
        }

        private void ListDragSource_GiveFeedback(object? sender, GiveFeedbackEventArgs e)
        {
            testOutputHelper.WriteLine($"Give feedback on drag source.");

            // Use custom cursors if the check box is checked.
            if (UseCustomCursorsCheck.Checked)
            {
                // Sets the custom cursor based upon the effect.
                e.UseDefaultCursors = false;
                if ((e.Effect & DragDropEffects.Move) == DragDropEffects.Move)
                    Cursor.Current = MyNormalCursor;
                else
                    Cursor.Current = MyNoDropCursor;
            }
        }

        private void ListDragTarget_DragOver(object? sender, DragEventArgs e)
        {
            testOutputHelper.WriteLine($"Drag over on the drag target.");

            // Determine whether string data exists in the drop data. If not, then
            // the drop effect reflects that the drop cannot occur.
            if (e.Data is null || !e.Data.GetDataPresent(typeof(string)))
            {
                e.Effect = DragDropEffects.None;
                DropLocationLabel.Text = "None - no string data.";
                return;
            }

            // Set the effect based upon the KeyState.
            if ((e.KeyState & (8 + 32)) == (8 + 32) &&
                (e.AllowedEffect & DragDropEffects.Link) == DragDropEffects.Link)
            {
                // KeyState 8 + 32 = CTRL + ALT

                // Link drag-and-drop effect.
                e.Effect = DragDropEffects.Link;
            }
            else if ((e.KeyState & 32) == 32 &&
                (e.AllowedEffect & DragDropEffects.Link) == DragDropEffects.Link)
            {
                // ALT KeyState for link.
                e.Effect = DragDropEffects.Link;
            }
            else if ((e.KeyState & 4) == 4 &&
                (e.AllowedEffect & DragDropEffects.Move) == DragDropEffects.Move)
            {
                // SHIFT KeyState for move.
                e.Effect = DragDropEffects.Move;
            }
            else if ((e.KeyState & 8) == 8 &&
                (e.AllowedEffect & DragDropEffects.Copy) == DragDropEffects.Copy)
            {
                // CTRL KeyState for copy.
                e.Effect = DragDropEffects.Copy;
            }
            else if ((e.AllowedEffect & DragDropEffects.Move) == DragDropEffects.Move)
            {
                // By default, the drop action should be move, if allowed.
                e.Effect = DragDropEffects.Move;
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }

            // Get the index of the item the mouse is below.

            // The mouse locations are relative to the screen, so they must be
            // converted to client coordinates.

            indexOfItemUnderMouseToDrop =
                ListDragTarget.IndexFromPoint(ListDragTarget.PointToClient(new Point(e.X, e.Y)));

            // Updates the label text.
            if (indexOfItemUnderMouseToDrop != ListBox.NoMatches)
            {
                DropLocationLabel.Text = "Drops before item #" + (indexOfItemUnderMouseToDrop + 1);
            }
            else
            {
                DropLocationLabel.Text = "Drops at the end.";
            }
        }

        private void ListDragTarget_DragDrop(object? sender, DragEventArgs e)
        {
            testOutputHelper.WriteLine($"Drag drop on drag target.");

            // Ensure that the list item index is contained in the data.
            if (e.Data is not null && e.Data.GetDataPresent(typeof(string)))
            {
                object? item = e.Data.GetData(typeof(string));

                // Perform drag-and-drop, depending upon the effect.
                if (item is not null && (e.Effect == DragDropEffects.Copy || e.Effect == DragDropEffects.Move))
                {
                    // Insert the item.
                    if (indexOfItemUnderMouseToDrop != ListBox.NoMatches)
                        ListDragTarget.Items.Insert(indexOfItemUnderMouseToDrop, item);
                    else
                        ListDragTarget.Items.Add(item);
                }
            }

            // Reset the label text.
            DropLocationLabel.Text = "None";
        }

        private void ListDragSource_QueryContinueDrag(object? sender, QueryContinueDragEventArgs e)
        {
            testOutputHelper.WriteLine($"Query for drag continuation.");

            // Cancel the drag if the mouse moves off the form.
            if (sender is ListBox lb)
            {
                Form form = lb.FindForm();

                // Cancel the drag if the mouse moves off the form. The screenOffset
                // takes into account any desktop bands that may be at the top or left
                // side of the screen.
                if (((Control.MousePosition.X - screenOffset.X) < form.DesktopBounds.Left) ||
                    ((Control.MousePosition.X - screenOffset.X) > form.DesktopBounds.Right) ||
                    ((Control.MousePosition.Y - screenOffset.Y) < form.DesktopBounds.Top) ||
                    ((Control.MousePosition.Y - screenOffset.Y) > form.DesktopBounds.Bottom))
                {
                    testOutputHelper.WriteLine($"Cancelling drag.");
                    e.Action = DragAction.Cancel;
                }
            }
        }

        private void ListDragTarget_DragEnter(object? sender, DragEventArgs e)
        {
            testOutputHelper.WriteLine($"Drag enter on target.");

            // Reset the label text.
            DropLocationLabel.Text = "None";
        }

        private void ListDragTarget_DragLeave(object? sender, EventArgs e)
        {
            testOutputHelper.WriteLine($"Drag leave on target.");

            // Reset the label text.
            DropLocationLabel.Text = "None";
        }
    }
}
