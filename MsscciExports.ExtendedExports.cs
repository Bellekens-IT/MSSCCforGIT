using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MSSCCforGIT;

public static partial class MsscciExports
{
    // -------------------------------------------------------------------------------------------
    // The following entry points are part of the mandatory MSSCCI export table. EA resolves every
    // one of them via GetProcAddress when the provider DLL is loaded ("InitProcPointers"); if any
    // is missing, EA refuses to initialize the provider at all. Where the underlying feature has
    // no direct git-native counterpart wired up yet, these are implemented as safe stubs.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Resolves the local project path for a given project name. Since Git has no separate
    /// "project" indirection, we just echo back the requested local path unchanged.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccGetProjPath", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccGetProjPath(
        IntPtr pContext,
        IntPtr hWnd,
        sbyte* lpProjName,
        sbyte* lpLocalPath,
        int nLocalPathLen,
        int* pbNew,
        int fFlags)
    {
        if (pbNew != null) *pbNew = 0; // Not a new project.
        return SCC_OK;
    }

    /// <summary>
    /// Reports which command-line style options this provider supports for a given command.
    /// We don't expose any provider-specific options dialog, so return 0 (no extra options).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccGetCommandOptions", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccGetCommandOptions(
        IntPtr pContext,
        IntPtr hWnd,
        int nCommand)
    {
        return SCC_OK;
    }

    /// <summary>
    /// Shows a diff for a file. Not yet implemented against LibGit2Sharp; report as not performed
    /// so EA can surface a clear message rather than silently doing nothing. Note: EA's "Compare
    /// with controlled version" menu option does its own internal diff (against a copy fetched via
    /// SccGet) rather than calling this export - confirmed via diagnostics showing SccDiff is
    /// never invoked for that command - so this is unused by EA's most common comparison flow.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccDiff", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccDiff(
        IntPtr pContext,
        IntPtr hWnd,
        sbyte* lpFileName,
        int fOptions)
    {
        return SCC_E_OPNOTPERFORMED;
    }

    /// <summary>
    /// Populates an EA project list (e.g., for Add/Remove file pickers) with files under source
    /// control. Not yet implemented; returns no additional files.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccPopulateList", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccPopulateList(
        IntPtr pContext,
        int nCommand,
        int nFiles,
        sbyte** lpFileNames,
        sbyte** lpFileNamesNew,
        int fOptions,
        IntPtr* ppFileList,
        ushort* pdwFlags)
    {
        if (ppFileList != null) *ppFileList = IntPtr.Zero;
        return SCC_OK;
    }

    /// <summary>
    /// Lets EA query which optional notification events this provider wants delivered
    /// (e.g., file added/renamed/deleted outside of SCC operations). We don't subscribe to any.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccGetEvents", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccGetEvents(IntPtr pContext)
    {
        return SCC_OK;
    }

    /// <summary>
    /// Sets a provider-specific option (e.g., a UI preference). We have no configurable options.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccSetOption", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccSetOption(
        IntPtr pContext,
        int nOption,
        nint dwVal)
    {
        return SCC_OK;
    }

    // -------------------------------------------------------------------------------------------
    // Extended MSSCCI exports. EA's provider loader resolves the FULL extended export table
    // (not just the base v1.1 set) via GetProcAddress before accepting a provider, matching what
    // other working Git MSSCCI providers (e.g. pbsGitMSSCCI) export. These are implemented as
    // safe no-ops / "not performed" stubs where no direct git-native behavior applies yet.
    // -------------------------------------------------------------------------------------------

    [UnmanagedCallersOnly(EntryPoint = "SccAddFromScc", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccAddFromScc(
        IntPtr pContext,
        IntPtr hWnd,
        int nFiles,
        sbyte** lpFileNames,
        sbyte* lpComment,
        int* pFlags,
        int fOptions)
    {
        string comment = lpComment != null ? ReadPlainAnsiString((IntPtr)lpComment) : "EA Add";
        if (string.IsNullOrWhiteSpace(comment)) comment = "EA Add";
        return AddCore(nFiles, lpFileNames, pContext, comment);
    }

    [UnmanagedCallersOnly(EntryPoint = "SccAddFilesFromSCC", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccAddFilesFromSCC(
        IntPtr pContext,
        IntPtr hWnd,
        int nFiles,
        sbyte** lpFileNames,
        sbyte* lpComment,
        int* pFlags,
        int fOptions)
    {
        string comment = lpComment != null ? ReadPlainAnsiString((IntPtr)lpComment) : "EA Add";
        if (string.IsNullOrWhiteSpace(comment)) comment = "EA Add";
        return AddCore(nFiles, lpFileNames, pContext, comment);
    }

    [UnmanagedCallersOnly(EntryPoint = "SccBackgroundGet", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccBackgroundGet(
        IntPtr pContext,
        IntPtr hWnd,
        int nFiles,
        sbyte** lpFileNames,
        int fOptions,
        IntPtr pvConfig)
    {
        return GetCore(nFiles, lpFileNames, pContext);
    }

    [UnmanagedCallersOnly(EntryPoint = "SccBeginBatch", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccBeginBatch(
        IntPtr pContext,
        IntPtr hWnd,
        int nCommand,
        int nFiles,
        sbyte** lpFileNames)
    {
        // Diagnostics confirmed EA never actually invokes this during a "checkin branch"; batching
        // is instead handled via the debounce mechanism in SccCheckin/FlushPendingCheckins. Logging
        // is kept here in case a future EA version (or a different caller) does use it.
        LogDiagnostic("SccBeginBatch", new Exception($"nCommand={nCommand} nFiles={nFiles}"));
        return SCC_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccEndBatch", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccEndBatch(
        IntPtr pContext,
        IntPtr hWnd,
        int nCommand)
    {
        LogDiagnostic("SccEndBatch", new Exception($"nCommand={nCommand}"));
        return SCC_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccCreateSubProject", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccCreateSubProject(
        IntPtr pContext,
        IntPtr hWnd,
        sbyte* lpProjPath,
        sbyte* lpNewSubProjName)
    {
        return SCC_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccDirDiff", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccDirDiff(
        IntPtr pContext,
        IntPtr hWnd,
        sbyte* lpDirName,
        int fOptions)
    {
        return SCC_E_OPNOTPERFORMED;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccDirQueryInfo", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccDirQueryInfo(
        IntPtr pContext,
        sbyte* lpDirName,
        int* pStatus)
    {
        if (pStatus != null) *pStatus = SCC_STATUS_CONTROLLED;
        return SCC_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccEnumChangedFiles", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccEnumChangedFiles(
        IntPtr pContext,
        IntPtr hWnd,
        sbyte* lpProjName,
        sbyte* lpLocalPath,
        int fOptions,
        IntPtr* ppFileList)
    {
        if (ppFileList != null) *ppFileList = IntPtr.Zero;
        return SCC_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccGetExtendedCapabilities", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccGetExtendedCapabilities(
        IntPtr pContext,
        int nID)
    {
        // No optional extended capabilities are supported.
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccGetParentProjectPath", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccGetParentProjectPath(
        IntPtr pContext,
        sbyte* lpProjName,
        sbyte* lpLocalPath,
        sbyte* lpParentProjName,
        int nParentProjNameLen)
    {
        if (lpParentProjName != null && nParentProjNameLen > 0)
        {
            *lpParentProjName = 0; // Empty string: no parent project.
        }
        return SCC_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccGetUserOption", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccGetUserOption(
        IntPtr pContext,
        IntPtr hWnd,
        int nOption)
    {
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccIsMultiCheckoutEnabled", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccIsMultiCheckoutEnabled(IntPtr pContext)
    {
        // Git has no exclusive checkout locking, so multi-checkout is always effectively enabled.
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccPopulateDirList", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccPopulateDirList(
        IntPtr pContext,
        sbyte* lpDirName,
        int fOptions,
        IntPtr* ppFileList)
    {
        if (ppFileList != null) *ppFileList = IntPtr.Zero;
        return SCC_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccQueryChanges", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccQueryChanges(
        IntPtr pContext,
        sbyte* lpProjName,
        int nFiles,
        sbyte** lpFileNames,
        int fOptions,
        IntPtr* ppFileList)
    {
        if (ppFileList != null) *ppFileList = IntPtr.Zero;
        return SCC_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccRemoveDir", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccRemoveDir(
        IntPtr pContext,
        IntPtr hWnd,
        sbyte* lpDirName,
        sbyte* lpComment,
        int fOptions)
    {
        return SCC_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "SccWillCreateSccFile", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccWillCreateSccFile(IntPtr pContext)
    {
        // Git doesn't create any provider-specific "scc" marker files.
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "DllRegisterServer", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int DllRegisterServer()
    {
        return SCC_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "DllUnregisterServer", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int DllUnregisterServer()
    {
        return SCC_OK;
    }
}
