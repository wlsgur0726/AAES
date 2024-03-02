using System.Threading;
using System.Threading.Tasks;

namespace AAES.Exceptions
{
    public class WaitingCanceledException : TaskCanceledException, ICanceledException
    {
        public WaitingCanceledException(CancellationToken token)
            : base(null, null, token)
        {
        }
    }
}
