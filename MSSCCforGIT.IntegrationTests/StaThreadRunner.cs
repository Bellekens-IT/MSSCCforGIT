using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace MSSCCforGIT.IntegrationTests;

/// <summary>
/// Hosts a single background thread set to the Single-Threaded Apartment (STA) with a running
/// Windows message pump, and marshals work onto it. EA's Automation COM server (EA.App /
/// EA.Repository) is an STA server and some of its methods (e.g. Package.VersionControlCheckout)
/// rely on the calling thread pumping Windows messages while the call is in progress - without
/// that, calls can fail with an opaque COMException (HRESULT 0x8004000D, CO_E_OBJNOTCONNECTED)
/// because the RPC channel to EA's STA object is torn down. xUnit test threads run on the
/// .NET thread pool in a Multi-Threaded Apartment (MTA) by default, so every EA call in these
/// integration tests must be routed through this runner instead of being made directly.
/// </summary>
public sealed class StaThreadRunner : IDisposable
{
    private readonly BlockingCollection<Action> _workItems = new();
    private readonly Thread _thread;

    public StaThreadRunner()
    {
        _thread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void RunMessageLoop()
    {
        foreach (var workItem in _workItems.GetConsumingEnumerable())
        {
            workItem();
        }
    }

    /// <summary>Runs the given function on the STA thread and returns its result, propagating any exception.</summary>
    public T Invoke<T>(Func<T> func)
    {
        T result = default!;
        ExceptionDispatchInfo? error = null;
        using var done = new ManualResetEventSlim(false);

        _workItems.Add(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                done.Set();
            }
        });

        done.Wait();
        error?.Throw();

        return result;
    }

    /// <summary>Runs the given action on the STA thread, propagating any exception.</summary>
    public void Invoke(Action action)
    {
        Invoke<object?>(() =>
        {
            action();
            return null;
        });
    }

    public void Dispose()
    {
        _workItems.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(10));
        _workItems.Dispose();
    }
}

