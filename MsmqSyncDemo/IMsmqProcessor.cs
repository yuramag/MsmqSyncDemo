using System.Threading;
using System.Threading.Tasks;

namespace MsmqSyncDemo
{
    public interface IMsmqProcessor
    {
        Task<string> ProcessAsync(string data, CancellationToken ct);
        void Cancel();
    }
}