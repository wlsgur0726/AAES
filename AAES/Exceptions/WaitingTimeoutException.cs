using System;

namespace AAES.Exceptions
{
    public class WaitingTimeoutException : TimeoutException, ICanceledException
    {
        public WaitingTimeoutException(TimeSpan timeout)
            : base($"Timeout {timeout}")
        {
            this.Timeout = timeout;
        }

        public TimeSpan Timeout { get; }
    }
}
