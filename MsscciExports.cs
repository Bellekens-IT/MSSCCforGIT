using LibGit2Sharp;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Linq;
using System.Collections.Generic;

namespace MSSCCforGIT;

public static partial class MsscciExports
{
    // MSSCCI Return Codes (SCC_OK, SCC_E_UNKNOWNERROR, etc.)
    private const int SCC_OK = 0;
    private const int SCC_E_UNKNOWNERROR = -1;
    private const int SCC_E_NONSPECIFICERROR = -2;
    private const int SCC_E_FILENOTCONTROLLED = -19;
    private const int SCC_E_NOTAUTHORIZED = -6;
    private const int SCC_E_INITIALIZATIONFAILED = -5;
    private const int SCC_E_OPNOTPERFORMED = -30;

    // SCC status bit flags (returned via SccQueryInfo)
    private const int SCC_STATUS_INVALID = -1;
    private const int SCC_STATUS_NOTCONTROLLED = 0x0000;
    private const int SCC_STATUS_CONTROLLED = 0x0001;
    private const int SCC_STATUS_CHECKEDOUT = 0x0002;
    private const int SCC_STATUS_OUTBYOTHER = 0x0004;
    private const int SCC_STATUS_OUTMULTIPLE = 0x0008;
    private const int SCC_STATUS_OUTEXCLUSIVE = 0x0010;
    private const int SCC_STATUS_DIFFERENT = 0x0080;
    private const int SCC_STATUS_NOTINPROJECT = 0x0200;

    private static bool s_nativeDllDirectoryInitialized;

    /// <summary>
    /// Runs automatically when this assembly is loaded (before any exported Scc* function can be
    /// invoked), ensuring our own directory is added to the native DLL search path so LibGit2Sharp's
    /// git2-*.dll (which sits next to us, not next to the host EA.exe) can be found. Wrapped in a
    /// try/catch because module initializers run unconditionally whenever this assembly is loaded
    /// into ANY host process - not just EA.exe - including the WinForms designer's own isolated
    /// "design tools server" process when opening HistoryForm/PropertiesForm in the visual
    /// designer. An unhandled exception here would abort loading the assembly in that process too,
    /// surfacing as "Failed to launch the design tools server process" - and this native DLL
    /// search path setup isn't needed there anyway (no git/LibGit2Sharp calls happen at design time).
    /// </summary>
#pragma warning disable CA2255 // Used intentionally in this native-hosted library to set up the
                               // DLL search path before EA calls into any exported Scc* function.
    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            EnsureNativeDllDirectory();
        }
        catch
        {
            // Best-effort only; EA's own Scc* calls will still fail clearly later (e.g. via
            // DllNotFoundException from LibGit2Sharp) if this truly didn't work, but we must not
            // let a module-initializer failure take down whatever process loaded this assembly.
        }
    }
#pragma warning restore CA2255

    /// <summary>
    /// When EA (or any host process) loads this provider DLL, the default DLL search path only
    /// includes the host EXE's directory - not ours. Since LibGit2Sharp's native git2-*.dll sits
    /// alongside our own DLL (not EA.exe), P/Invokes into it fail with DllNotFoundException unless
    /// we explicitly add our own directory to the search path first.
    /// </summary>
    private static unsafe void EnsureNativeDllDirectory()
    {
        if (s_nativeDllDirectoryInitialized) return;

        nint codeAddress = (nint)(delegate*<void>)&EnsureNativeDllDirectoryMarker;

        if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, codeAddress, out IntPtr hModule) && hModule != IntPtr.Zero)
        {
            Span<char> buffer = stackalloc char[1024];
            int len;
            fixed (char* pBuffer = buffer)
            {
                len = GetModuleFileNameW(hModule, pBuffer, buffer.Length);
            }

            if (len > 0)
            {
                string modulePath = new string(buffer.Slice(0, len));
                string? directory = Path.GetDirectoryName(modulePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    AddDllDirectory(directory);
                    SetDllDirectory(directory);
                }
            }
        }

        s_nativeDllDirectoryInitialized = true;
    }

    /// <summary>
    /// Dummy marker method whose JITted/AOT-compiled code address is used purely to identify
    /// which native module (our DLL) this code lives in via GetModuleHandleEx.
    /// </summary>
    private static void EnsureNativeDllDirectoryMarker() { }

    private const uint GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x00000004;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetModuleHandleExW(uint dwFlags, nint lpModuleName, out IntPtr phModule);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static unsafe extern int GetModuleFileNameW(IntPtr hModule, char* lpFilename, int nSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr AddDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    /// <summary>
    /// EA calls this first to get the MSSCCI spec version supported (e.g., version 1.3 -> 0x00010300).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccGetVersion", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static int SccGetVersion()
    {
        return 0x00010300; // MSSCCI v1.3
    }

    /// <summary>
    /// Called when EA initializes the SCC context.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccInitialize", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccInitialize(
        IntPtr* ppContext,
        IntPtr hWnd,
        sbyte* lpCallerName,
        sbyte* lpSccName,
        int* lpSccCaps,
        sbyte* lpAuxPathLabel,
        int* pnCheckoutCommentLen,
        int* pnCommentLen)
    {
        try
        {
            // Write provider name into the buffer EA provides
            string providerName = "Git Native MSSCCI";
            Marshal.Copy(System.Text.Encoding.ASCII.GetBytes(providerName + '\0'), 0, (IntPtr)lpSccName, providerName.Length + 1);

            // Set flags capabilities (e.g., supports checkouts, comments)
            if (lpSccCaps != null)
            {
                *lpSccCaps = 0x00000001; // SCC_CAP_CHECKOUT
            }

            // EA expects these to report the max buffer lengths it should allocate for
            // checkout/checkin comment text; leaving them unset can confuse the caller.
            if (pnCheckoutCommentLen != null) *pnCheckoutCommentLen = 2048;
            if (pnCommentLen != null) *pnCommentLen = 2048;

            // EA stores this context handle and passes it back on every subsequent call.
            // We don't need per-session state, but it must be a non-null, stable value.
            if (ppContext != null) *ppContext = (IntPtr)1;

            return SCC_OK;
        }
        catch (Exception ex)
        {
            LogDiagnostic("SccInitialize", ex);
            return SCC_E_INITIALIZATIONFAILED;
        }
    }

    /// <summary>
    /// Temporary diagnostic helper: writes exception details to a log file next to the DLL so we
    /// can see the real cause of failures that would otherwise be swallowed as generic SCC error codes.
    /// </summary>
    private static void LogDiagnostic(string context, Exception ex)
    {
        try
        {
            string logPath = Path.Combine(Path.GetTempPath(), "MSSCCforGIT.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:O}] {context}: {ex}\n\n");
        }
        catch
        {
            // Best-effort logging only.
        }
    }

    /// <summary>
    /// Called when EA shuts down the SCC context.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccUninitialize", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccUninitialize(IntPtr pContext)
    {
        return SCC_OK;
    }

    /// <summary>
    /// Called by EA to open/associate a project with a local path. For Git there's no
    /// separate "project" concept, so we just verify a repository exists at the path.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccOpenProject", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccOpenProject(
        IntPtr pContext,
        IntPtr hWnd,
        sbyte* lpUser,
        sbyte* lpProjName,
        sbyte* lpLocalPath,
        sbyte* lpAuxProjPath,
        sbyte* lpComment,
        sbyte* lpTextOut,
        int nTextOutLen,
        int fOption)
    {
        try
        {
            string? projName = lpProjName != null ? Marshal.PtrToStringAnsi((IntPtr)lpProjName) : null;
            string? localPath = lpLocalPath != null ? Marshal.PtrToStringAnsi((IntPtr)lpLocalPath) : null;

            // Prefer the local path if given; some EA versions only populate lpProjName with the
            // path instead. Accept whichever one resolves to a git repository.
            string? candidatePath = !string.IsNullOrEmpty(localPath) ? localPath : projName;
            if (!string.IsNullOrEmpty(candidatePath))
            {
                // Normalize to a full path with trailing directory separator; libgit2's discovery
                // API can behave inconsistently with relative paths or missing trailing slashes.
                candidatePath = Path.GetFullPath(candidatePath);
                if (!candidatePath.EndsWith(Path.DirectorySeparatorChar))
                {
                    candidatePath += Path.DirectorySeparatorChar;
                }
            }

            string? discovered = null;
            try
            {
                discovered = candidatePath != null ? DiscoverRepositoryPath(candidatePath) : null;
            }
            catch (Exception discoverEx)
            {
                LogDiagnostic("SccOpenProject.Discover", discoverEx);
            }

            LogDiagnostic("SccOpenProject", new Exception(
                $"projName='{projName}' localPath='{localPath}' candidatePath='{candidatePath}' discoveredLen={discovered?.Length.ToString() ?? "null"} discovered='{discovered}'"));

            if (string.IsNullOrEmpty(candidatePath) || string.IsNullOrEmpty(discovered))
            {
                return SCC_E_NONSPECIFICERROR;
            }

            s_projectRootsByContext[pContext] = candidatePath!;
            s_lastOpenedProjectRoot = candidatePath;

            return SCC_OK;
        }
        catch (Exception ex)
        {
            LogDiagnostic("SccOpenProject", ex);
            return SCC_E_UNKNOWNERROR;
        }
    }

    /// <summary>
    /// Called when EA closes the project. No git-specific work required.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "SccCloseProject", CallConvs = new[] { typeof(CallConvStdcall) })]
    public static unsafe int SccCloseProject(IntPtr pContext)
    {
        return SCC_OK;
    }
}
