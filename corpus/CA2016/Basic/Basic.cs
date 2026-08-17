// CA2016: Forward the 'CancellationToken' parameter to methods. The token
// the method receives is never passed to the cancellable callee.
using System.Threading;
using System.Threading.Tasks;

public static class UnforwardedCancellationToken
{
    public static Task WaitAsync(CancellationToken cancellationToken) =>
        Task.Delay(1000);
}
