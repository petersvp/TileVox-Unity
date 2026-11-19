using System.Collections.Generic;
using TileVox.Meshing;

namespace WorldFlow
{
    /// <summary>An edge set and its modifier-specific distance in mesh units.</summary>
    public struct EdgeSelection
    {
        public IList<Edge> edges;
        public double parameter;

        public EdgeSelection(IList<Edge> edges, double parameter)
        {
            this.edges = edges;
            this.parameter = parameter;
        }
    }
}
