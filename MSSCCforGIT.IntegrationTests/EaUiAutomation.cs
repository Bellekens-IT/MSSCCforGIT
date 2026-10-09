using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace MSSCCforGIT.IntegrationTests;

/// <summary>
/// Drives EA's actual UI (Project Browser tree + right-click context menus) via Windows UI
/// Automation (through FlaUI), rather than EA's COM Automation API, for version-control actions.
///
/// This exists because EA's Automation methods for version control (e.g.
/// Package.VersionControlCheckout/VersionControlCheckin) were found to throw an opaque
/// COMException (HRESULT 0x8004000D, CO_E_OBJNOTCONNECTED) when called via COM, even though the
/// same action works when triggered the way a real user would, through EA's own UI - confirmed to
/// reproduce the same way against a TFS-backed version-controlled package, so it's an EA
/// Automation-API limitation unrelated to this provider. Simulating the real user action through
/// UI Automation sidesteps it entirely.
///
/// All calls here must be made from the fixture's STA thread (see <see cref="StaThreadRunner"/>),
/// same as the COM calls, since UI Automation client calls are also apartment-sensitive.
/// </summary>
public sealed class EaUiAutomation : IDisposable
{
    private readonly Application _application;
    private readonly UIA3Automation _automation;

    public EaUiAutomation(int processId)
    {
        _automation = new UIA3Automation();
        _application = Application.Attach(processId);
    }

    private Window MainWindow => _application.GetMainWindow(_automation)
        ?? throw new InvalidOperationException("Could not find EA's main window.");

    /// <summary>
    /// Brings EA's main window to the foreground. Must be called before any simulated mouse input
    /// (e.g. right-clicking a tree item) - FlaUI's mouse actions click at absolute screen
    /// coordinates, so if EA isn't the foreground/topmost window at that location, the click lands
    /// on whatever window actually occupies those screen coordinates instead (observed: a
    /// right-click meant for EA's Project Browser landed in a web browser window instead).
    /// </summary>
    public void BringToForeground()
    {
        var window = MainWindow;
        if (window.Patterns.Window.IsSupported)
        {
            window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
        }

        window.Focus();
        Thread.Sleep(200);
    }

    /// <summary>
    /// Gets the Project Browser tree item that is currently selected. Callers should first select
    /// the desired package via <c>Repository.ShowInProjectView(package)</c> (COM) before calling
    /// this, which is far more reliable than searching the tree by name since EA's Project Browser
    /// can contain many packages/diagrams/elements and tree traversal by name is slow and fragile.
    /// </summary>
    public AutomationElement GetSelectedTreeItem()
    {
        // FindProjectBrowserTree() scans the whole main window and is relatively expensive, so find
        // it once and only retry the (cheap) selection check - ShowInProjectView updates the Project
        // Browser's selection asynchronously relative to the COM call returning.
        var tree = FindProjectBrowserTree();

        for (int attempt = 0; attempt < 20; attempt++)
        {
            var selected = FindSelectedItemRecursive(tree.Items);
            if (selected is not null)
            {
                return selected;
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException("No Project Browser tree item is currently selected.");
    }

    private static TreeItem? FindSelectedItemRecursive(TreeItem[] items)
    {
        foreach (var item in items)
        {
            bool isSelected;
            try
            {
                isSelected = item.IsSelected;
            }
            catch
            {
                isSelected = false;
            }

            if (isSelected)
            {
                return item;
            }

            // Only descend into expanded nodes - collapsed subtrees can't contain the current
            // selection (EA expands the path to a package when ShowInProjectView selects it).
            TreeItem[] children;
            try
            {
                children = item.ExpandCollapseState == ExpandCollapseState.Expanded ? item.Items : Array.Empty<TreeItem>();
            }
            catch
            {
                children = Array.Empty<TreeItem>();
            }

            var found = FindSelectedItemRecursive(children);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the actual Project Browser tree control in EA's main window. EA exposes several tree
    /// controls (e.g. the Perspectives selector, which has many flat root items like "ArcGIS",
    /// "SysML", etc.); the Project Browser is identified by its single root item named "Model".
    /// </summary>
    private Tree FindProjectBrowserTree()
    {
        var trees = MainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Tree));
        if (trees.Length == 0)
        {
            throw new InvalidOperationException("Could not find any Tree control in EA's main window.");
        }

        var projectBrowserTree = trees
            .Select(t => t.AsTree())
            .FirstOrDefault(t => t.Items.Length == 1 && string.Equals(t.Items[0].Name, "Model", StringComparison.OrdinalIgnoreCase));

        return projectBrowserTree
            ?? throw new InvalidOperationException("Could not identify the Project Browser tree control (expected a single 'Model' root item).");
    }

    /// <summary>
    /// Navigates EA's right-click "Package Control" submenu and activates "Check Out..." - purely
    /// via simulated keyboard input (arrow keys/Enter through <see cref="Keyboard"/>, which sends
    /// real OS-level input events), not via any UIA/COM query of the open menu.
    ///
    /// This exists because reading the open menu's contents through UI Automation (walking its raw
    /// tree, or any FindAllChildren/FindFirstChild call against it) was found to reliably freeze EA
    /// solid (its main window starts reporting "Not Responding" and never recovers) - native Win32
    /// popup menus run their own modal message loop via TrackPopupMenu on EA's UI thread, and a
    /// same-process UIA provider call back into that thread while the loop is pumping appears to
    /// deadlock it. Keyboard input has no such problem: Win32 menus are designed to be driven
    /// entirely via arrow keys/mnemonics/Enter without any introspection required, so this never
    /// calls back into EA at all while its menu is open.
    ///
    /// Must be called immediately after the right-click that opened the menu, with the context
    /// menu's "Package Control" item require being reached by 12 Down presses from the top (EA's
    /// package context menu for a version-controlled package, top to bottom: Specialize,
    /// Collaborate, Properties, Specification Manager, Open Package in, Import Viewpoint File,
    /// Model Builder, New Package, New Diagram, New Element, Add as Favorite to, Package Control -
    /// confirmed against a screenshot of the real menu; the first Down press selects item 1, so
    /// landing on item 12 needs 12 presses, not 11 - confirmed by observing a live run land one
    /// item short, on "Add as Favorite to"). From there, Right opens the "Package Control"
    /// submenu, and 5 more Down presses select "Check Out..." (Compare, Package Baselines...,
    /// Check In Branch..., Check Out Branch..., Check In..., Check Out...).
    /// </summary>
    public void InvokeCheckOutFromOpenContextMenu()
    {
        for (int i = 0; i < 12; i++)
        {
            Keyboard.Press(VirtualKeyShort.DOWN);
            Thread.Sleep(100);
        }

        Keyboard.Press(VirtualKeyShort.RIGHT);
        Thread.Sleep(200);

        for (int i = 0; i < 5; i++)
        {
            Keyboard.Press(VirtualKeyShort.DOWN);
            Thread.Sleep(100);
        }

        Keyboard.Press(VirtualKeyShort.ENTER);
    }

    public void Dispose()
    {
        _automation.Dispose();
    }
}
