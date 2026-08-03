using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SerialTerminal.Prefabs
{
    /// <summary>
    /// Identity comparer for the prefab reference sweep: the objects walked there
    /// may override Equals/GetHashCode, and the sweep must track the instances it
    /// has already visited, not values that compare equal.
    /// </summary>
    internal sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new();

        bool IEqualityComparer<object>.Equals(object x, object y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
