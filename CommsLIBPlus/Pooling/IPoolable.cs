using System;
using System.Collections.Generic;
using System.Text;

namespace CommsLIBPlus.Pooling
{
    public interface IPoolable : IDisposable
    {
        abstract void Reset();

        /// <summary>
        /// Returns object to Pool
        /// </summary>
        /// <example>
        /// <code>
        /// Pool<T>.Push(this);
        /// GC.SuppressFinalize(this);
        /// </code>
        /// </example>
        abstract new void Dispose();
    }
}
