using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AAES.Exceptions
{
    public class DeadlockDetectedException : Exception
    {
        public DeadlockDetectedException(IReadOnlyList<object> deadlockNodeList)
            : base(new StringBuilder()
                  .AppendLine("deadlock detected.")
                  .AppendJoin(Environment.NewLine, deadlockNodeList.Select(e => $"  {e}"))
                  .ToString())
        {
            this.DeadlockNodeList = deadlockNodeList;
        }

        public IReadOnlyList<object> DeadlockNodeList { get; }
    }
}
