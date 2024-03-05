using System;

namespace AAES.Exceptions
{
    public class NotHeldResourceException : InvalidOperationException
    {
        public NotHeldResourceException(AAESResource resource)
        {
            this.Resource = resource;
        }

        public AAESResource Resource { get; }
    }
}
