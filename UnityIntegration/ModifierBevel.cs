// Uncomment to color the bevel passes for inspection.
// #define MODIFIER_BEVEL_DEBUG_COLORS

using g4;
using System;
using System.Collections.Generic;
using TileVox.Meshing;

namespace WorldFlow
{
    /// <summary>Bevels selected voxel mesh edges while keeping the original edge groups available to later modifiers.</summary>
    /// <remarks>
    /// Rebuilds the quad adjacency, offsets the selected sides, cuts the faces at their ends,
    /// builds the strips, then closes supported junctions using the resulting boundaries.
    /// </remarks>
    public class ModifierBevel
    {
#if MODIFIER_BEVEL_DEBUG_COLORS
        // Packed RGBA colors for the cutter, offsets, strips, and the three fill paths.
        private const uint CutColor = 0xFF0000FF;
        private const uint MovedColor = 0xFFFF00FF;
        private const uint BridgeColor = 0x00FF00FF;
        private const uint EndPatchColor = 0x00FFFFFF;
        private const uint CornerPatchColor = 0x0000FFFF;
        private const uint CutJunctionPatchColor = 0x005500FF;
#endif

        private class SurfaceQuad
        {
            // Original and inset corners in the source winding order. Side i runs from i to (i + 1) & 3.
            public Vector3d[] points;
            public Vector3d[] moved;
            public SurfaceEdge[] edges = new SurfaceEdge[4];
            // Equal positions share an ID. The fan separates surface sheets at that position.
            public int[] vertexIds = new int[4];
            public int[] vertexFans = new int[4];
            public int gridCorners;
            // Corner cut distances are measured along the outgoing (U) and incoming (V) sides.
            public double[] notchU = new double[4], notchV = new double[4];
            public bool[] outerProfile = new bool[4];
            public SideCapOffsets[] sideCaps;
            // Two optional support points per side and one optional cut curve per corner.
            public Vector3d?[] sideSupports;
            public List<Vector3d>[] cutProfiles;
            // Source normal and appearance, reused after the quad is offset or cut.
            public Vector3d normal;
            public uint color, material;
        }

        // The individual insets locate a paired cap. The full notch dimensions preserve the L profile.
        private struct SideCapOffsets
        {
            public double insetU, insetV;
            public double notchU, notchV;
        }

        private class SurfaceEdge
        {
            // The first quad supplies the direction from a to b. The second quad usually winds in reverse.
            public Vector3d a, b;
            public SurfaceQuad first, second;
            public int firstSide, secondSide;
            // A contact split recovers two independent edges from four sides at the same positions.
            public bool selected, nonManifold, splitContact;
            public double width;
            // A stitched end shares a curve with another strip. A supported end has its own planar cap.
            public BridgeProfile profileA, profileB;
            public BridgeEndPatch capA, capB;
        }

        // Both strips reference the same sample array. One may traverse it in the opposite order.
        private struct BridgeProfile
        {
            public Vector3d[] points;
            public bool reversed;
        }

        private class BridgeEndPatch
        {
            // The surrounding faces own the two halves of the cap. A single owner can supply both.
            public SurfaceQuad firstSource, secondSource;
            // A compound junction may insert a support vertex on either leg of this cap.
            public Vector3d? firstSupport, secondSupport;
        }

        private struct JunctionBorder
        {
            // Directed as the missing patch will traverse it, opposite the surrounding surface.
            public Vector3d a, b;
            public uint color, material;
            // A seam can need a support vertex on a source face or on a neighboring end patch.
            public SurfaceQuad support;
            public int supportSide;
            public Vector3d supportPoint;
            public BridgeEndPatch capSupport;
            public bool firstCapLeg;
            // Distinguishes strips and face cuts from plain seam segments during fill and attribution.
            public bool followsCut, followsBridge;
            public SurfaceEdge bridge;
        }

        // The input stays intact. The collections below describe the surface for the current Apply call.
        private readonly StructureMesherResult3D source;
        private readonly List<SurfaceQuad> quads = new();
        private readonly List<SurfaceEdge> edges = new();
        private readonly Dictionary<(double x, double y, double z), int> vertices = new();
        private readonly Dictionary<(int a, int b), SurfaceEdge> edgeLookup = new();
        private readonly Dictionary<int, List<(SurfaceQuad quad, int corner)>> incidentQuads = new();
        private readonly Dictionary<int, List<SurfaceEdge>> incidentEdges = new();

        /// <summary>Edge groups and their widths. An empty selection uses all sharp manifold edges.</summary>
        public IList<EdgeSelection> edgeSets;

        /// <summary>Bevel width in mesh units when edges are selected automatically.</summary>
        public double bevelDistance = 0.2;

        /// <summary>Number of profile spans. The sharp L profile requires at least two.</summary>
        public int bevelSegments = 1;

        /// <summary>Profile shape. Zero is an L, 0.5 is round, and one returns the source mesh.</summary>
        public double profileRoundness = 0.5;

        /// <summary>Uses the supplied block faces and their classified edge groups as input.</summary>
        public ModifierBevel(StructureMesherResult3D source)
        {
            this.source = source;
        }

        /// <summary>Returns the rebuilt source faces, bevel strips, and supported junction fills.</summary>
        public StructureMesherResult3D Apply()
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (profileRoundness >= 1) return source;
            quads.Clear();
            edges.Clear();
            vertices.Clear();
            edgeLookup.Clear();
            incidentQuads.Clear();
            incidentEdges.Clear();

            // Resolve which face sides really meet before classifying cuts or building strips.
            ImportQuads();
            BuildAdjacency();
            BuildVertexFans();
            SelectEdges();
            CutJunctionFaces();
            CutSideCaps();

            var output = new List<Face>(quads.Count);
            List<NgonFace> outputNgons = null;
            foreach (var quad in quads)
            {
                Vector3d center = (quad.points[0] + quad.points[1] + quad.points[2] + quad.points[3]) * 0.25;
                var moved = quad.moved = new Vector3d[4];
                for (int i = 0; i < 4; i++) moved[i] = OffsetCorner(quad, i, center);
                BuildCutProfiles(quad);
            }

            int bridgeSteps = ProfileSteps();
            foreach (var entry in incidentEdges)
                StitchBridgeEnds(entry.Key, entry.Value, bridgeSteps);

            List<TriangleFace> bridgeTriangles = null;
            foreach (var edge in edges)
                if (HasBridge(edge))
                    BridgeMovedEdge(edge, bridgeSteps, output, ref bridgeTriangles);

            // Junctions can request support vertices on a face or a pending cap, so emit those last.
            foreach (var entry in incidentQuads)
                FillJunctions(entry.Key, entry.Value, bridgeSteps, output, ref bridgeTriangles);

            foreach (var edge in edges)
                if (edge.capA != null || edge.capB != null)
                    EmitBridgeEndPatches(edge, bridgeSteps, ref bridgeTriangles);

            // A single buffer of indices serves each quad before its optional support points are inserted.
            var cornerStart = new int[4];
            var cornerEnd = new int[4];
            foreach (var quad in quads)
            {
                Vector3d[] moved = quad.moved;
                bool needsSupports = quad.sideSupports != null;
                bool hasCut = quad.cutProfiles != null;
                List<Vector3d> outline = null;
                if (needsSupports || hasCut)
                {
                    outline = new List<Vector3d>(8);
                    for (int i = 0; i < 4; i++)
                    {
                        cornerStart[i] = outline.Count;
                        List<Vector3d> profile = quad.cutProfiles?[i];
                        if (profile == null) outline.Add(moved[i]);
                        else outline.AddRange(profile);
                        cornerEnd[i] = outline.Count - 1;
                    }
                    if (needsSupports) InsertSideSupports(quad, outline, cornerStart, cornerEnd);
                }
#if MODIFIER_BEVEL_DEBUG_COLORS
                uint color = hasCut ? CutColor : QuadWasMoved(quad, moved) ? MovedColor : quad.color;
#else
                uint color = quad.color;
#endif
                if (outline == null)
                    output.Add(new Face(moved[0], moved[1], moved[2], moved[3], color, quad.material));
                else
                {
                    outputNgons ??= source.Ngons == null
                        ? new List<NgonFace>() : new List<NgonFace>(source.Ngons);
                    outputNgons.Add(new NgonFace(outline, color, quad.material));
                }
            }

            // Keep the source boundary groups at their original positions for downstream modifiers.
            return new StructureMesherResult3D
            {
                Quads = output,
                Ngons = outputNgons ?? source.Ngons,
                Triangles = bridgeTriangles ?? source.Triangles,
                LedgeBoundaries = source.LedgeBoundaries,
                BaseBoundaries = source.BaseBoundaries,
                WallToCeilingBoundaries = source.WallToCeilingBoundaries,
                CeilingToWallBoundaries = source.CeilingToWallBoundaries,
                WallCornerBoundaries = source.WallCornerBoundaries,
                MaterialBoundaries = source.MaterialBoundaries,
                ColorBoundaries = source.ColorBoundaries
            };
        }

        /// <summary>Samples the portion of each corner notch that remains after the two incident sides have been offset.</summary>
        private void BuildCutProfiles(SurfaceQuad quad)
        {
            for (int i = 0; i < 4; i++)
            {
                Vector3d origin = quad.points[i];
                Vector3d moved = quad.moved[i];
                Vector3d outgoing = (quad.points[(i + 1) & 3] - origin).Normalized;
                Vector3d incoming = (quad.points[(i + 3) & 3] - origin).Normalized;
                double notchU = quad.notchU[i], notchV = quad.notchV[i];
                SideCapOffsets sideCaps = quad.sideCaps == null ? default : quad.sideCaps[i];
                bool meetsTwoSideCaps = sideCaps.insetU > 1e-12 && sideCaps.insetV > 1e-12;
                if (meetsTwoSideCaps)
                {
                    // At a paired ending each neighboring strip supplies a different axis of the arc.
                    notchU = profileRoundness > 0 ? sideCaps.insetU : Math.Max(notchU, sideCaps.notchU);
                    notchV = profileRoundness > 0 ? sideCaps.insetV : Math.Max(notchV, sideCaps.notchV);
                }
                // The inset sides already remove part of the notch. Only cut the remaining area.
                double u = notchU - Vector3d.Dot(moved - origin, outgoing);
                double v = notchV - Vector3d.Dot(moved - origin, incoming);
                if (u <= 1e-12 || v <= 1e-12) continue;
                bool outer = (quad.outerProfile[i] || meetsTwoSideCaps) && profileRoundness > 0;
                double startAngle = 0, endAngle = 0;
                if (outer && !TryGetOuterAngles(origin, moved, outgoing, incoming,
                    notchU, notchV, out startAngle, out endAngle)) continue;

                var profile = new List<Vector3d>();
                if (outer)
                    AppendOuterProfile(profile, origin, outgoing, incoming, notchU, notchV, startAngle, endAngle);
                else AppendInnerProfile(profile, moved, incoming * v, outgoing * u);
                quad.cutProfiles ??= new List<Vector3d>[4];
                quad.cutProfiles[i] = profile;
            }
        }

        /// <summary>Converts source quads to working faces, splitting grid rectangles into tiles so selected boundary spans can be resolved individually.</summary>
        private void ImportQuads()
        {
            if (source.Quads == null) return;
            foreach (var face in source.Quads)
            {
                Vector3d u = face.b - face.a;
                Vector3d v = face.d - face.a;
                if ((face.c - face.b - face.d + face.a).LengthSquared > 1e-16 ||
                    !AxisAligned(u) || !AxisAligned(v))
                {
                    AddQuad(face.a, face.b, face.c, face.d, face.color, face.material);
                    continue;
                }

                // Split merged block faces at voxel edges so selected boundary spans can
                // shrink independently, even when they cover only part of a large quad.
                int columns = TileCount(u), rows = TileCount(v);
                Vector3d column = u / columns, row = v / rows;
                for (int y = 0; y < rows; y++)
                    for (int x = 0; x < columns; x++)
                    {
                        Vector3d a = face.a + column * x + row * y;
                        AddQuad(a, a + column, a + column + row, a + row,
                            face.color, face.material);
                    }
            }
        }

        /// <summary>Returns the number of whole voxel units along a side so an integer rectangle can be split evenly.</summary>
        private static int TileCount(Vector3d side)
        {
            double length = side.Length;
            int count = (int)Math.Round(length);
            return count > 1 && Math.Abs(length - count) < 1e-8 ? count : 1;
        }

        /// <summary>Checks that a side has a nonzero component on exactly one coordinate axis.</summary>
        private static bool AxisAligned(Vector3d side)
        {
            int axes = (Math.Abs(side.x) > 1e-8 ? 1 : 0) +
                (Math.Abs(side.y) > 1e-8 ? 1 : 0) + (Math.Abs(side.z) > 1e-8 ? 1 : 0);
            return axes == 1;
        }

        /// <summary>Stores a working quad with the source winding, surface normal, and appearance.</summary>
        private void AddQuad(Vector3d a, Vector3d b, Vector3d c, Vector3d d,
            uint color, uint material)
        {
            Vector3d normal = Vector3d.Cross(b - a, c - a);
            quads.Add(new SurfaceQuad
            {
                points = new[] { a, b, c, d },
                normal = normal.LengthSquared > 1e-20 ? normal.Normalized : Vector3d.Zero,
                color = color,
                material = material
            });
        }

        /// <summary>Assigns geometric vertex IDs and matches quad sides by their unordered pair of endpoints.</summary>
        private void BuildAdjacency()
        {
            Dictionary<SurfaceEdge, List<(SurfaceQuad quad, int side)>> contacts = null;
            foreach (var quad in quads)
                for (int corner = 0; corner < 4; corner++)
                {
                    int id = GetVertex(quad.points[corner]);
                    quad.vertexIds[corner] = id;
                    if (!incidentQuads.TryGetValue(id, out var incident))
                    {
                        incident = new List<(SurfaceQuad quad, int corner)>();
                        incidentQuads.Add(id, incident);
                    }
                    incident.Add((quad, corner));
                }

            foreach (var quad in quads)
                for (int side = 0; side < 4; side++)
                {
                    int a = quad.vertexIds[side];
                    int b = quad.vertexIds[(side + 1) & 3];
                    var key = a < b ? (a, b) : (b, a);
                    if (!edgeLookup.TryGetValue(key, out SurfaceEdge edge))
                    {
                        edge = new SurfaceEdge
                        {
                            a = quad.points[side],
                            b = quad.points[(side + 1) & 3]
                        };
                        edgeLookup.Add(key, edge);
                        edges.Add(edge);
                        AddIncidentEdge(a, edge);
                        AddIncidentEdge(b, edge);
                    }
                    if (edge.first == null)
                    {
                        edge.first = quad;
                        edge.firstSide = side;
                    }
                    else if (edge.second == null)
                    {
                        edge.second = quad;
                        edge.secondSide = side;
                    }
                    else
                    {
                        contacts ??= new();
                        if (!contacts.TryGetValue(edge, out var sides))
                        {
                            sides = new List<(SurfaceQuad quad, int side)>(4)
                            {
                                (edge.first, edge.firstSide), (edge.second, edge.secondSide)
                            };
                            contacts.Add(edge, sides);
                        }
                        sides.Add((quad, side));
                        edge.nonManifold = true;
                    }
                    quad.edges[side] = edge;
                }

            if (contacts != null)
                foreach (var contact in contacts)
                    SplitEdgeContact(contact.Key, contact.Value);
        }

        /// <summary>Pairs the four sides at a diagonal voxel contact into two separate convex surface edges.</summary>
        private void SplitEdgeContact(SurfaceEdge edge, List<(SurfaceQuad quad, int side)> sides)
        {
            if (sides.Count != 4) return;
            // At a diagonal voxel contact, each solid quadrant contributes one convex pair.
            for (int i = 1; i < sides.Count; i++)
            {
                int j = i == 1 ? 2 : 1;
                int k = 6 - i - j;
                if (!FormsConvexFold(sides[0], sides[i]) || !FormsConvexFold(sides[j], sides[k]))
                    continue;

                var separate = new SurfaceEdge();
                SetContactPair(edge, sides[0], sides[i]);
                SetContactPair(separate, sides[j], sides[k]);
                edges.Add(separate);
                AddIncidentEdge(separate.first.vertexIds[separate.firstSide], separate);
                AddIncidentEdge(separate.first.vertexIds[(separate.firstSide + 1) & 3], separate);
                return;
            }
        }

        /// <summary>Reattaches two physical face sides to one recovered edge and adopts the direction of the first side.</summary>
        private static void SetContactPair(SurfaceEdge edge, (SurfaceQuad quad, int side) first,
            (SurfaceQuad quad, int side) second)
        {
            edge.first = first.quad;
            edge.second = second.quad;
            edge.firstSide = first.side;
            edge.secondSide = second.side;
            edge.a = first.quad.points[first.side];
            edge.b = first.quad.points[(first.side + 1) & 3];
            edge.nonManifold = false;
            edge.splitContact = true;
            first.quad.edges[first.side] = edge;
            second.quad.edges[second.side] = edge;
        }

        /// <summary>Uses opposite face winding and inward directions to identify the outside fold around one solid voxel quadrant.</summary>
        private static bool FormsConvexFold((SurfaceQuad quad, int side) first,
            (SurfaceQuad quad, int side) second)
        {
            return first.quad.vertexIds[first.side] == second.quad.vertexIds[(second.side + 1) & 3] &&
                Vector3d.Dot(first.quad.normal, FaceInward(second.quad, second.side)) < -1e-12 &&
                Vector3d.Dot(second.quad.normal, FaceInward(first.quad, first.side)) < -1e-12;
        }

        /// <summary>Groups the face corners at each geometric vertex by connectivity through manifold edges.</summary>
        private void BuildVertexFans()
        {
            foreach (var entry in incidentQuads)
            {
                var faces = entry.Value;
                // Start with one fan per face corner and merge only across real surface edges.
                for (int i = 0; i < faces.Count; i++)
                {
                    var (quad, corner) = faces[i];
                    quad.vertexFans[corner] = i;
                }
                if (!incidentEdges.TryGetValue(entry.Key, out var localEdges)) continue;
                foreach (var edge in localEdges)
                {
                    if (edge.nonManifold || edge.second == null) continue;
                    int firstFan = EdgeFan(edge, entry.Key);
                    int secondFan = edge.second.vertexFans[CornerAt(edge.second, edge.secondSide, entry.Key)];
                    if (firstFan == secondFan) continue;
                    foreach (var (quad, corner) in faces)
                        if (quad.vertexFans[corner] == secondFan)
                            quad.vertexFans[corner] = firstFan;
                }
            }
        }

        /// <summary>Finds the local surface fan that owns this edge at the supplied endpoint.</summary>
        private static int EdgeFan(SurfaceEdge edge, int vertex) =>
            edge.first.vertexFans[CornerAt(edge.first, edge.firstSide, vertex)];

        /// <summary>Finds the corner index at the requested endpoint of a known face side.</summary>
        private static int CornerAt(SurfaceQuad quad, int side, int vertex) =>
            quad.vertexIds[side] == vertex ? side : (side + 1) & 3;

        /// <summary>Looks up or assigns an ID for a position shared by one or more source face corners.</summary>
        private int GetVertex(Vector3d point)
        {
            var key = (point.x, point.y, point.z);
            if (vertices.TryGetValue(key, out int id)) return id;
            id = vertices.Count;
            vertices.Add(key, id);
            return id;
        }

        /// <summary>Registers a surface edge at one endpoint for local cut and junction searches.</summary>
        private void AddIncidentEdge(int vertex, SurfaceEdge edge)
        {
            if (!incidentEdges.TryGetValue(vertex, out var incident))
            {
                incident = new List<SurfaceEdge>();
                incidentEdges.Add(vertex, incident);
            }
            incident.Add(edge);
        }

        /// <summary>Applies the selected edge groups and takes the widest matching width when groups overlap.</summary>
        private void SelectEdges()
        {
            bool automatic = edgeSets == null || edgeSets.Count == 0;
            foreach (var edge in edges)
            {
                if (edge.first == null || edge.second == null || edge.nonManifold) continue;
                if (automatic)
                {
                    edge.selected = Vector3d.Dot(edge.first.normal, edge.second.normal) < 0.9999;
                    edge.width = bevelDistance;
                    continue;
                }

                foreach (var set in edgeSets)
                {
                    if (set.edges == null || (edge.selected && !(set.parameter > edge.width))) continue;
                    if (edge.splitContact && MatchesContactGroup(edge, set.edges) is bool matchesGroup)
                    {
                        if (matchesGroup)
                        {
                            edge.selected = true;
                            edge.width = set.parameter;
                        }
                        continue;
                    }
                    foreach (var selected in set.edges)
                        if (ContainsEdge(selected, edge.a, edge.b))
                        {
                            edge.selected = true;
                            edge.width = set.parameter;
                            break;
                        }
                }
            }
        }

        /// <summary>Tests a recovered contact edge against a built-in group, or returns null for a caller-supplied edge list.</summary>
        private bool? MatchesContactGroup(SurfaceEdge edge, IList<Edge> selection)
        {
            // The mesher omits edges with four incident faces from its boundary lists.
            // Classify the two recovered convex folds separately for the same edge groups.
            double firstY = edge.first.normal.y, secondY = edge.second.normal.y;
            if (ReferenceEquals(selection, source.LedgeBoundaries))
                return Math.Max(firstY, secondY) > 0.9999;
            if (ReferenceEquals(selection, source.CeilingToWallBoundaries))
                return Math.Min(firstY, secondY) < -0.9999;
            if (ReferenceEquals(selection, source.WallCornerBoundaries))
                return Math.Abs(firstY) < 1e-8 && Math.Abs(secondY) < 1e-8;
            if (ReferenceEquals(selection, source.BaseBoundaries) ||
                ReferenceEquals(selection, source.WallToCeilingBoundaries))
                return false;
            if (ReferenceEquals(selection, source.MaterialBoundaries))
                return edge.first.material != edge.second.material;
            if (ReferenceEquals(selection, source.ColorBoundaries))
                return edge.first.color != edge.second.color;
            return null;
        }

        /// <summary>Checks whether both candidate endpoints lie on a source segment, including merged boundary spans.</summary>
        private static bool ContainsEdge(Edge selection, Vector3d a, Vector3d b)
        {
            Vector3d line = selection.b - selection.a;
            double lengthSquared = line.LengthSquared;
            if (lengthSquared < 1e-20) return false;
            double aParameter = Vector3d.Dot(a - selection.a, line) / lengthSquared;
            double bParameter = Vector3d.Dot(b - selection.a, line) / lengthSquared;
            if (aParameter < -1e-8 || aParameter > 1 + 1e-8 ||
                bParameter < -1e-8 || bParameter > 1 + 1e-8) return false;
            return Vector3d.Cross(a - selection.a, line).LengthSquared < lengthSquared * 1e-16 &&
                Vector3d.Cross(b - selection.a, line).LengthSquared < lengthSquared * 1e-16;
        }

        /// <summary>Finds corner cuts on faces perpendicular to a selected edge and joined to both of its incident faces.</summary>
        private void CutJunctionFaces()
        {
            foreach (var entry in incidentQuads)
            {
                int vertex = entry.Key;
                if (!incidentEdges.TryGetValue(vertex, out var junctionEdges)) continue;
                MarkBridgeGridCorners(vertex, entry.Value, junctionEdges);
                foreach (var (quad, corner) in entry.Value)
                {
                    if ((quad.gridCorners & (1 << corner)) != 0) continue;
                    Vector3d point = quad.points[corner];
                    Vector3d outgoing = (quad.points[(corner + 1) & 3] - point).Normalized;
                    Vector3d incoming = (quad.points[(corner + 3) & 3] - point).Normalized;
                    double u = 0, v = 0;
                    int participatingCount = 0;
                    foreach (var edge in junctionEdges)
                    {
                        if (!edge.selected || edge.width == 0) continue;
                        if (edge.first == quad || edge.second == quad)
                        {
                            participatingCount++;
                            continue;
                        }
                        // A perpendicular cap needs both adjoining faces in the same corner.
                        if (!SharesEdge(quad, edge.first) ||
                            !SharesEdge(quad, edge.second)) continue;
                        Vector3d direction = (edge.b - edge.a).Normalized;
                        if (Math.Abs(Vector3d.Dot(quad.normal, direction)) < 0.9999)
                            continue;
                        double edgeU = 0, edgeV = 0;
                        AccumulateInset(SideOffsetAtVertex(edge.first, edge, vertex),
                            outgoing, incoming, ref edgeU, ref edgeV);
                        AccumulateInset(SideOffsetAtVertex(edge.second, edge, vertex),
                            outgoing, incoming, ref edgeU, ref edgeV);
                        if (edgeU <= 1e-12 || edgeV <= 1e-12) continue;
                        participatingCount++;
                        u = Math.Max(u, edgeU);
                        v = Math.Max(v, edgeV);
                    }
                    if (u > 1e-12 && v > 1e-12)
                    {
                        quad.notchU[corner] = u;
                        quad.notchV[corner] = v;
                        quad.outerProfile[corner] = participatingCount >= 3;
                    }
                }
            }
        }

        /// <summary>Reserves three-sided corners and four-sided bridge crossings for grids before cutting faces.</summary>
        private static void MarkBridgeGridCorners(int vertex, List<(SurfaceQuad quad, int corner)> faces,
            List<SurfaceEdge> localEdges)
        {
            foreach (var (quad, corner) in faces)
            {
                if ((quad.gridCorners & (1 << corner)) != 0 ||
                    !HasBridge(quad.edges[corner]) || !HasBridge(quad.edges[(corner + 3) & 3])) continue;
                int fan = quad.vertexFans[corner];
                int faceCount = 0, bridgeCount = 0, convexCount = 0;
                bool closed = true, coplanarSeams = true;
                foreach (var (face, index) in faces)
                {
                    if (face.vertexFans[index] != fan) continue;
                    faceCount++;
                    SurfaceEdge incoming = face.edges[(index + 3) & 3], outgoing = face.edges[index];
                    closed &= incoming.second != null && outgoing.second != null &&
                        !incoming.nonManifold && !outgoing.nonManifold;
                }
                if (!closed) continue;

                foreach (var edge in localEdges)
                {
                    if (EdgeFan(edge, vertex) != fan) continue;
                    if (HasBridge(edge))
                    {
                        bridgeCount++;
                        if (FormsConvexFold((edge.first, edge.firstSide), (edge.second, edge.secondSide)))
                            convexCount++;
                    }
                    else
                        coplanarSeams &= HasCoplanarNeighbor(edge.first, edge);
                }

                bool threeSided = faceCount == 3 && bridgeCount == 3 && convexCount == 3;
                bool fourSided = bridgeCount == 4 && coplanarSeams;
                if (!threeSided && !fourSided) continue;

                // The strips define the junction boundary. An unrelated corner notch would clip their rails.
                foreach (var (face, index) in faces)
                    if (face.vertexFans[index] == fan) face.gridCorners |= 1 << index;
            }
        }

        /// <summary>Projects a neighboring face inset into the target corner directions and keeps the largest positive extent.</summary>
        private static void AccumulateInset(Vector3d inset, Vector3d outgoing,
            Vector3d incoming, ref double u, ref double v)
        {
            u = Math.Max(u, Vector3d.Dot(inset, outgoing));
            v = Math.Max(v, Vector3d.Dot(inset, incoming));
        }

        /// <summary>Checks whether two faces share a usable surface edge after the diagonal contacts have been separated.</summary>
        private static bool SharesEdge(SurfaceQuad first, SurfaceQuad second)
        {
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    if (first.edges[i] == second.edges[j] && !first.edges[i].nonManifold)
                        return true;
            return false;
        }

        /// <summary>Finds the cuts needed in coplanar faces where selected edge runs end.</summary>
        private void CutSideCaps()
        {
            foreach (var edge in edges)
            {
                if (!edge.selected || edge.width <= 0) continue;
                CutSideCap(edge, edge.a, edge.b);
                CutSideCap(edge, edge.b, edge.a);
            }
        }

        /// <summary>Classifies the fold at the end of a selected edge and rules out a straight continuation in the same surface fan.</summary>
        private void CutSideCap(SurfaceEdge edge, Vector3d point, Vector3d otherEnd)
        {
            if (!vertices.TryGetValue((point.x, point.y, point.z), out int vertex) ||
                !incidentQuads.TryGetValue(vertex, out var incident)) return;
            Vector3d firstInset = SideOffsetAtVertex(edge.first, edge, vertex);
            Vector3d secondInset = SideOffsetAtVertex(edge.second, edge, vertex);
            double firstFold = Vector3d.Dot(edge.first.normal, secondInset);
            double secondFold = Vector3d.Dot(edge.second.normal, firstInset);
            // A convex fold insets both faces behind the neighboring plane. A concave fold does the reverse.
            bool convex = firstFold < -1e-12 && secondFold < -1e-12;
            bool concave = firstFold > 1e-12 && secondFold > 1e-12;
            if (!convex && (!concave || edge.nonManifold)) return;
            Vector3d extension = (point - otherEnd).Normalized;
            if (incidentEdges.TryGetValue(vertex, out var neighbors))
                foreach (var neighbor in neighbors)
                {
                    if (neighbor == edge || !HasBridge(neighbor) || EdgeFan(neighbor, vertex) != EdgeFan(edge, vertex))
                        continue;
                    Vector3d neighborEnd = (neighbor.a - point).LengthSquared < 1e-20
                        ? neighbor.b : neighbor.a;
                    if (Vector3d.Dot(extension, (neighborEnd - point).Normalized) > 0.9999)
                        return;
                }

            CutSideCapFromFace(edge.first, edge, vertex, point, extension, incident, convex);
            CutSideCapFromFace(edge.second, edge, vertex, point, extension, incident, convex);
        }

        /// <summary>Transfers the terminal inset and its extension into a coplanar adjoining face when the local boundary permits it.</summary>
        private static void CutSideCapFromFace(SurfaceQuad face, SurfaceEdge edge, int vertex,
            Vector3d point, Vector3d extension,
            IList<(SurfaceQuad quad, int corner)> incident, bool convex)
        {
            Vector3d inset = SideOffsetAtVertex(face, edge, vertex);
            foreach (var (quad, corner) in incident)
            {
                if ((quad.gridCorners & (1 << corner)) != 0 || quad == face || !SharesEdge(quad, face) ||
                    Vector3d.Dot(quad.normal, face.normal) < 0.9999)
                    continue;
                Vector3d outgoing = (quad.points[(corner + 1) & 3] - point).Normalized;
                Vector3d incoming = (quad.points[(corner + 3) & 3] - point).Normalized;
                double extensionU = Vector3d.Dot(extension, outgoing) * edge.width;
                double extensionV = Vector3d.Dot(extension, incoming) * edge.width;
                double insetU = Vector3d.Dot(inset, outgoing);
                double insetV = Vector3d.Dot(inset, incoming);
                SurfaceEdge outgoingEdge = quad.edges[corner];
                SurfaceEdge incomingEdge = quad.edges[(corner + 3) & 3];
                // An unselected inside fold is where the bridge stops, not a path for its cut.
                bool alongU = extensionU > 1e-12 && insetV > 1e-12 && !IsUnbeveledInsideFold(outgoingEdge);
                bool alongV = extensionV > 1e-12 && insetU > 1e-12 && !IsUnbeveledInsideFold(incomingEdge);
                if (!alongU && !alongV) continue;

                double notchU = Math.Max(extensionU, insetU);
                double notchV = Math.Max(extensionV, insetV);
                quad.sideCaps ??= new SideCapOffsets[4];
                ref var sideCaps = ref quad.sideCaps[corner];
                if (alongU) sideCaps.insetV = Math.Max(sideCaps.insetV, insetV);
                if (alongV) sideCaps.insetU = Math.Max(sideCaps.insetU, insetU);
                sideCaps.notchU = Math.Max(sideCaps.notchU, notchU);
                sideCaps.notchV = Math.Max(sideCaps.notchV, notchV);

                // A concave ending only contributes to a cut paired with a perpendicular ending.
                if (!convex) continue;

                // A bevel ending over a flat seam needs a second end to form a cut here.
                bool reachesBoundary = (alongU && !HasCoplanarNeighbor(quad, outgoingEdge)) ||
                    (alongV && !HasCoplanarNeighbor(quad, incomingEdge));
                if (!reachesBoundary) continue;

                quad.notchU[corner] = Math.Max(quad.notchU[corner], notchU);
                quad.notchV[corner] = Math.Max(quad.notchV[corner], notchV);
                int participatingCount = 1;
                if (quad.edges[corner].selected && quad.edges[corner].width > 0)
                    participatingCount++;
                if (quad.edges[(corner + 3) & 3].selected &&
                    quad.edges[(corner + 3) & 3].width > 0)
                    participatingCount++;
                quad.outerProfile[corner] |= participatingCount >= 3;
            }
        }

        /// <summary>Identifies an unselected concave crease, including a wall meeting a sharp floor or ceiling.</summary>
        private static bool IsUnbeveledInsideFold(SurfaceEdge edge)
        {
            if (edge.first == null || edge.second == null || edge.nonManifold ||
                (edge.selected && edge.width != 0)) return false;

            Vector3d firstInward = FaceInward(edge.first, edge.firstSide);
            Vector3d secondInward = FaceInward(edge.second, edge.secondSide);
            return Vector3d.Dot(edge.first.normal, secondInward) > 1e-12 &&
                Vector3d.Dot(edge.second.normal, firstInward) > 1e-12;
        }

        /// <summary>Returns the direction across a face from a side, using the face normal and its winding.</summary>
        private static Vector3d FaceInward(SurfaceQuad quad, int side) =>
            Vector3d.Cross(quad.normal, (quad.points[(side + 1) & 3] - quad.points[side]).Normalized);

        /// <summary>Checks whether another face on the same plane continues this side.</summary>
        private static bool HasCoplanarNeighbor(SurfaceQuad quad, SurfaceEdge edge)
        {
            SurfaceQuad neighbor = edge.first == quad ? edge.second : edge.second == quad ? edge.first : null;
            return neighbor != null && Vector3d.Dot(quad.normal, neighbor.normal) > 0.9999;
        }

        /// <summary>Reads the in-plane offset for a selected side incident to the specified vertex.</summary>
        private static Vector3d SideOffsetAtVertex(SurfaceQuad quad, SurfaceEdge edge, int vertex)
        {
            Vector3d center = (quad.points[0] + quad.points[1] + quad.points[2] + quad.points[3]) * 0.25;
            for (int side = 0; side < 4; side++)
                if (quad.edges[side] == edge &&
                    (quad.vertexIds[side] == vertex || quad.vertexIds[(side + 1) & 3] == vertex))
                    return SideOffset(quad, side, center);
            return Vector3d.Zero;
        }

        /// <summary>Applies the two incident side offsets to obtain the corner before any profile cut.</summary>
        private static Vector3d OffsetCorner(SurfaceQuad quad, int corner, Vector3d center)
        {
            Vector3d point = quad.points[corner];
            OffsetSide(quad, corner, center, ref point);
            OffsetSide(quad, (corner + 3) & 3, center, ref point);
            return point;
        }

        /// <summary>Adds the displacement supplied by one face side to a working corner.</summary>
        private static void OffsetSide(SurfaceQuad quad, int side, Vector3d center,
            ref Vector3d point)
        {
            point += SideOffset(quad, side, center);
        }

        /// <summary>Offsets a selected side toward the interior of its source face in mesh units.</summary>
        private static Vector3d SideOffset(SurfaceQuad quad, int side, Vector3d center)
        {
            SurfaceEdge edge = quad.edges[side];
            if (!edge.selected || edge.width == 0) return Vector3d.Zero;
            Vector3d a = quad.points[side], b = quad.points[(side + 1) & 3];
            Vector3d along = (b - a).Normalized;
            Vector3d toCenter = center - (a + b) * 0.5;
            Vector3d inward = toCenter - along * Vector3d.Dot(toCenter, along);
            if (inward.LengthSquared > 1e-20)
                return inward.Normalized * edge.width;
            return Vector3d.Zero;
        }

        /// <summary>Checks whether a usable selected edge has a nonzero strip width.</summary>
        private static bool HasBridge(SurfaceEdge edge) => edge.selected && edge.width != 0 && !edge.nonManifold;

        /// <summary>Finds a pair of bridge ends in a surface fan that can join at a turn.</summary>
        private void StitchBridgeEnds(int vertex, List<SurfaceEdge> incident, int steps)
        {
            for (int i = 0; i < incident.Count; i++)
            {
                SurfaceEdge first = incident[i], second = null;
                if (!HasBridge(first)) continue;
                int fan = EdgeFan(first, vertex);
                for (int j = 0; j < incident.Count; j++)
                {
                    SurfaceEdge edge = incident[j];
                    if (j == i || !HasBridge(edge) || EdgeFan(edge, vertex) != fan) continue;
                    if (j < i || second != null)
                    {
                        second = null;
                        break;
                    }
                    second = edge;
                }
                if (second != null) StitchBridgePair(vertex, first, second, steps);
            }
        }

        /// <summary>Matches the original rail anchors and averages the paired interior samples to make one shared profile.</summary>
        private void StitchBridgePair(int vertex, SurfaceEdge first, SurfaceEdge second, int steps)
        {
            Vector3d firstAxis = (first.b - first.a).Normalized;
            Vector3d secondAxis = (second.b - second.a).Normalized;
            if (Math.Abs(Vector3d.Dot(firstAxis, secondAxis)) > 0.9999) return;

            bool firstAtA = first.first.vertexIds[first.firstSide] == vertex;
            bool secondAtA = second.first.vertexIds[second.firstSide] == vertex;
            var firstRails = GetBridgeRails(first);
            var secondRails = GetBridgeRails(second);
            Vector3d firstStart = firstAtA ? firstRails.a0 : firstRails.b0;
            Vector3d firstEnd = firstAtA ? firstRails.a1 : firstRails.b1;
            Vector3d secondStart = secondAtA ? secondRails.a0 : secondRails.b0;
            Vector3d secondEnd = secondAtA ? secondRails.a1 : secondRails.b1;
            if (SameBridgePoint(firstStart, firstEnd)) return;

            bool reversed = SameBridgePoint(firstStart, secondEnd) && SameBridgePoint(firstEnd, secondStart);
            if (!reversed && (!SameBridgePoint(firstStart, secondStart) ||
                !SameBridgePoint(firstEnd, secondEnd))) return;
            if (!ContinuesRail(first.first, reversed ? second.second : second.first, vertex) ||
                !ContinuesRail(first.second, reversed ? second.first : second.second, vertex)) return;
            // The matching anchors and connected source rails identify a single strip turning the corner.
            Vector3d origin = firstAtA ? first.a : first.b;
            var points = new Vector3d[steps + 1];
            points[0] = firstStart;
            points[steps] = firstEnd;
            // Close the turn with the existing strip samples, keeping both rail anchors fixed.
            for (int i = 1; i < steps; i++)
                points[i] = (BridgePoint(origin, firstAxis, firstStart, firstEnd, i, steps) +
                    BridgePoint(origin, secondAxis, secondStart, secondEnd, reversed ? steps - i : i, steps)) * 0.5;

            var firstProfile = new BridgeProfile { points = points };
            var secondProfile = new BridgeProfile { points = points, reversed = reversed };
            if (firstAtA) first.profileA = firstProfile;
            else first.profileB = firstProfile;
            if (secondAtA) second.profileA = secondProfile;
            else second.profileB = secondProfile;
        }

        /// <summary>Checks whether two strip sides meet on the same face or continue through an unbridged edge at the vertex.</summary>
        private static bool ContinuesRail(SurfaceQuad first, SurfaceQuad second, int vertex)
        {
            if (first == second) return true;
            for (int side = 0; side < 4; side++)
            {
                SurfaceEdge edge = first.edges[side];
                if (HasBridge(edge) || edge.nonManifold ||
                    (first.vertexIds[side] != vertex && first.vertexIds[(side + 1) & 3] != vertex))
                    continue;
                if ((edge.first == first && edge.second == second) ||
                    (edge.first == second && edge.second == first)) return true;
            }
            return false;
        }

        /// <summary>Compares positions within the small tolerance used for independently sampled strip and face boundaries.</summary>
        private static bool SameBridgePoint(Vector3d a, Vector3d b) => (a - b).LengthSquared < 1e-20;

        /// <summary>Returns the two inset sides and puts their corresponding endpoints in the same order.</summary>
        private static (Vector3d a0, Vector3d b0, Vector3d a1, Vector3d b1) GetBridgeRails(SurfaceEdge edge)
        {
            int firstA = edge.firstSide, firstB = (firstA + 1) & 3;
            int secondA = edge.secondSide, secondB = (secondA + 1) & 3;
            // Match the rails by their original vertices, even if face order changes along a run.
            if (edge.first.vertexIds[firstA] != edge.second.vertexIds[secondA])
                (secondA, secondB) = (secondB, secondA);
            return (edge.first.moved[firstA], edge.first.moved[firstB],
                edge.second.moved[secondA], edge.second.moved[secondB]);
        }

        /// <summary>Visits each junction once per surface fan, so partitions that only touch remain separate.</summary>
        private void FillJunctions(int vertex, List<(SurfaceQuad quad, int corner)> faces, int steps,
            List<Face> quads, ref List<TriangleFace> triangles)
        {
            if (!incidentEdges.TryGetValue(vertex, out var localEdges)) return;
            for (int i = 0; i < localEdges.Count; i++)
            {
                if (!HasBridge(localEdges[i])) continue;
                int fan = EdgeFan(localEdges[i], vertex);
                bool firstInFan = true;
                for (int j = 0; j < i; j++)
                    if (HasBridge(localEdges[j]) && EdgeFan(localEdges[j], vertex) == fan)
                    {
                        firstInFan = false;
                        break;
                    }
                if (firstInFan) FillJunction(vertex, fan, faces, localEdges, steps, quads, ref triangles);
            }
        }

        /// <summary>Collects the missing boundary for a supported corner or cut transition and emits a patch only when it closes.</summary>
        private void FillJunction(int vertex, int fan, List<(SurfaceQuad quad, int corner)> faces,
            List<SurfaceEdge> edgesAtVertex, int steps, List<Face> quads, ref List<TriangleFace> triangles)
        {
            int faceCount = 0, bridgeCount = 0;
            bool hasCut = false, allConvex = true, gridCorner = false;
            SurfaceEdge firstBridge = null, secondBridge = null;
            Vector3d origin = default;
            foreach (var (quad, corner) in faces)
            {
                if (quad.vertexFans[corner] != fan) continue;
                faceCount++;
                origin = quad.points[corner];
                hasCut |= quad.cutProfiles?[corner] != null;
                gridCorner |= (quad.gridCorners & (1 << corner)) != 0;
                SurfaceEdge incoming = quad.edges[(corner + 3) & 3], outgoing = quad.edges[corner];
                if (incoming.nonManifold || outgoing.nonManifold ||
                    incoming.second == null || outgoing.second == null) return;
            }
            foreach (var edge in edgesAtVertex)
            {
                if (!HasBridge(edge) || EdgeFan(edge, vertex) != fan) continue;
                if (BridgeCapAt(edge, vertex) != null) continue;
                if (bridgeCount++ == 0) firstBridge = edge;
                else if (bridgeCount == 2) secondBridge = edge;
                if (!hasCut)
                    allConvex &= FormsConvexFold((edge.first, edge.firstSide), (edge.second, edge.secondSide));
            }

            bool threeBridgeCorner = faceCount == 3 && bridgeCount == 3 && allConvex;
            bool fourBridgeCorner = gridCorner && bridgeCount == 4 && !hasCut;
            bool bridgeTurn = bridgeCount == 2 && ShareBridgeFace(firstBridge, secondBridge);
            if (bridgeCount == 0 || (!hasCut && !threeBridgeCorner && !fourBridgeCorner && !bridgeTurn)) return;
            var borders = new List<JunctionBorder>();
            if (hasCut)
                foreach (var (quad, corner) in faces)
                {
                    if (quad.vertexFans[corner] != fan || quad.cutProfiles?[corner] is not { } profile)
                        continue;
                    for (int i = 1; i < profile.Count; i++)
                        AddJunctionBorder(borders, new JunctionBorder
                        {
                            a = profile[i], b = profile[i - 1], color = quad.color,
                            material = quad.material, followsCut = true
                        });
                }

            foreach (var edge in edgesAtVertex)
            {
                if (EdgeFan(edge, vertex) != fan) continue;
                if (HasBridge(edge))
                {
                    if (BridgeCapAt(edge, vertex) != null) continue;
                    if (!AddBridgeBorders(borders, edge, vertex, steps)) return;
                }
                else if ((hasCut || fourBridgeCorner || bridgeTurn) && !AddSeamBorder(borders, edge, vertex, origin))
                    return;
            }
            if (!hasCut && bridgeTurn)
            {
                // The two bridge profiles close against the two legs of the same hard corner.
                int hardLegs = 0;
                foreach (var border in borders)
                {
                    if (border.followsBridge) continue;
                    if (!SameBridgePoint(border.a, origin) && !SameBridgePoint(border.b, origin)) return;
                    hardLegs++;
                }
                if (hardLegs != 2) return;
            }
            if (hasCut)
            {
                bool touchesCut = false, touchesBridge = false;
                foreach (var border in borders)
                {
                    touchesCut |= border.followsCut;
                    touchesBridge |= border.followsBridge;
                }
                if (!touchesCut || !touchesBridge) return;
            }

            List<JunctionBorder> loop = OrderJunctionBorders(borders);
            if (loop == null) return;
            if (threeBridgeCorner && !hasCut && profileRoundness > 0)
            {
                FillThreeBridgeGrid(loop, steps, quads, ref triangles);
                return;
            }
            if (fourBridgeCorner)
            {
                if (FillFourBridgeGrid(loop, steps, quads, ref triangles))
                    RegisterJunctionSupports(loop, vertex);
                return;
            }
            List<TriangleFace> fill = TriangulateJunction(loop, hasCut);
            if (fill == null) return;

            // Insert support vertices only after the whole fill has been accepted.
            RegisterJunctionSupports(loop, vertex);
            triangles ??= source.Triangles == null
                ? new List<TriangleFace>() : new List<TriangleFace>(source.Triangles);
            triangles.AddRange(fill);
        }

        /// <summary>Splits an adjoining face or a pending cap at a vertex the accepted patch shares.</summary>
        private static void RegisterJunctionSupports(List<JunctionBorder> loop, int vertex)
        {
            foreach (var border in loop)
                if (border.capSupport != null)
                {
                    if (border.firstCapLeg) border.capSupport.firstSupport = border.supportPoint;
                    else border.capSupport.secondSupport = border.supportPoint;
                }
                else if (border.support != null)
                    AddSideSupport(border.support, border.supportSide, vertex, border.supportPoint);
        }

        /// <summary>Adds the exposed end of a strip in the winding needed by its neighboring patch.</summary>
        private bool AddBridgeBorders(List<JunctionBorder> borders, SurfaceEdge edge, int vertex, int steps)
        {
            bool atA = edge.first.vertexIds[edge.firstSide] == vertex;
            var (a0, b0, a1, b1) = GetBridgeRails(edge);
            Vector3d first = atA ? a0 : b0, second = atA ? a1 : b1;
            if (!SameBridgePoint(first, OutlineAnchor(edge.first, edge.firstSide, vertex)) ||
                !SameBridgePoint(second, OutlineAnchor(edge.second, edge.secondSide, vertex)))
                return false;
            Vector3d origin = atA ? edge.a : edge.b;
            Vector3d axis = (edge.b - edge.a).Normalized;
            BridgeProfile profile = atA ? edge.profileA : edge.profileB;
            Vector3d previous = profile.points == null ? first : profile.points[profile.reversed ? steps : 0];
            for (int i = 1; i <= steps; i++)
            {
                Vector3d next = profile.points == null ? BridgePoint(origin, axis, first, second, i, steps)
                    : profile.points[profile.reversed ? steps - i : i];
                SurfaceQuad owner = BridgeSource(edge, i, steps);
                AddJunctionBorder(borders, new JunctionBorder
                {
                    a = atA ? next : previous,
                    b = atA ? previous : next,
                    color = owner.color,
                    material = owner.material,
                    followsBridge = true,
                    bridge = edge
                });
                previous = next;
            }
            return true;
        }

        /// <summary>Returns the endpoint of the actual face side, accounting for a corner that was cut back.</summary>
        private static Vector3d OutlineAnchor(SurfaceQuad quad, int side, int vertex)
        {
            bool outgoing = quad.vertexIds[side] == vertex;
            int corner = outgoing ? side : (side + 1) & 3;
            List<Vector3d> profile = quad.cutProfiles?[corner];
            return profile == null ? quad.moved[corner] : profile[outgoing ? profile.Count - 1 : 0];
        }

        /// <summary>Checks whether two strips meet on one original face.</summary>
        private static bool ShareBridgeFace(SurfaceEdge first, SurfaceEdge second) =>
            first.first == second.first || first.first == second.second ||
            first.second == second.first || first.second == second.second;

        /// <summary>Gets the pending end patch at a specific original endpoint.</summary>
        private static BridgeEndPatch BridgeCapAt(SurfaceEdge edge, int vertex) =>
            edge.first.vertexIds[edge.firstSide] == vertex ? edge.capA : edge.capB;

        /// <summary>Finds a cap that extends this face side back to its original corner, and identifies the matching cap leg.</summary>
        private static BridgeEndPatch CapAlongSide(SurfaceQuad quad, int side, int vertex, out bool firstLeg)
        {
            int otherSide = quad.vertexIds[side] == vertex ? (side + 3) & 3 : (side + 1) & 3;
            SurfaceEdge bridge = quad.edges[otherSide];
            firstLeg = bridge.first == quad;
            return BridgeCapAt(bridge, vertex);
        }

        /// <summary>Adds the unmatched portion of a shared side and records where the longer side needs a support vertex.</summary>
        private static bool AddSeamBorder(List<JunctionBorder> borders, SurfaceEdge edge, int vertex, Vector3d origin)
        {
            bool atA = edge.first.vertexIds[edge.firstSide] == vertex;
            Vector3d firstAnchor = OutlineAnchor(edge.first, edge.firstSide, vertex);
            Vector3d secondAnchor = OutlineAnchor(edge.second, edge.secondSide, vertex);
            BridgeEndPatch firstCap = CapAlongSide(edge.first, edge.firstSide, vertex, out bool firstLeg);
            BridgeEndPatch secondCap = CapAlongSide(edge.second, edge.secondSide, vertex, out bool secondLeg);
            // Cyan extends that side to the original corner, including when the support face was moved.
            Vector3d first = firstCap == null ? firstAnchor : origin;
            Vector3d second = secondCap == null ? secondAnchor : origin;
            if (SameBridgePoint(first, second)) return true;
            if (!ContainsEdge(new Edge(edge.a, edge.b), first, second)) return false;

            Vector3d inward = atA ? edge.b - edge.a : edge.a - edge.b;
            // The nearer anchor belongs to the longer side and leaves an exposed segment up to the other anchor.
            bool firstLonger = Vector3d.Dot(second - first, inward) > 0;
            SurfaceQuad support = firstLonger ? edge.first : edge.second;
            Vector3d supportPoint = firstLonger ? second : first;
            BridgeEndPatch cap = firstLonger ? firstCap : secondCap;
            if (cap != null && !IsInteriorSupport(origin, firstLonger ? firstAnchor : secondAnchor, supportPoint))
                cap = null;
            AddJunctionBorder(borders, new JunctionBorder
            {
                a = atA ? second : first,
                b = atA ? first : second,
                color = support.color,
                material = support.material,
                support = support,
                supportSide = firstLonger ? edge.firstSide : edge.secondSide,
                supportPoint = supportPoint,
                capSupport = cap,
                firstCapLeg = firstLonger ? firstLeg : secondLeg
            });
            return true;
        }

        /// <summary>Adds a directed boundary segment and cancels it against an existing segment traversed in reverse.</summary>
        private static void AddJunctionBorder(List<JunctionBorder> borders, JunctionBorder edge)
        {
            if (SameBridgePoint(edge.a, edge.b)) return;
            for (int i = 0; i < borders.Count; i++)
                if (SameBridgePoint(edge.a, borders[i].b) && SameBridgePoint(edge.b, borders[i].a))
                {
                    borders.RemoveAt(i);
                    return;
                }
            borders.Add(edge);
        }

        /// <summary>Orders a unique closed loop while rejecting open chains, branches, and multiple loops.</summary>
        private static List<JunctionBorder> OrderJunctionBorders(List<JunctionBorder> borders)
        {
            if (borders.Count < 3) return null;
            int count = borders.Count;
            var loop = new List<JunctionBorder>(count) { borders[count - 1] };
            borders.RemoveAt(count - 1);
            while (borders.Count != 0)
            {
                Vector3d end = loop[loop.Count - 1].b;
                if (SameBridgePoint(end, loop[0].a)) return null;
                int next = -1;
                for (int i = 0; i < borders.Count; i++)
                    if (SameBridgePoint(end, borders[i].a))
                    {
                        if (next >= 0) return null;
                        next = i;
                    }
                if (next < 0) return null;
                loop.Add(borders[next]);
                borders.RemoveAt(next);
            }
            return SameBridgePoint(loop[count - 1].b, loop[0].a) ? loop : null;
        }

        /// <summary>Builds a Coons quad grid between four strip ends, preserving any connecting seam samples.</summary>
        private bool FillFourBridgeGrid(List<JunctionBorder> loop, int steps, List<Face> quads,
            ref List<TriangleFace> triangles)
        {
            int start = 0;
            while (start < loop.Count && (loop[start].bridge == null ||
                loop[start].bridge == loop[(start + loop.Count - 1) % loop.Count].bridge)) start++;
            if (start == loop.Count) return false;

            var sides = new int[5];
            int count = 0;
            for (int side = 0; side < 4; side++)
            {
                if (count >= loop.Count) return false;
                SurfaceEdge bridge = loop[(start + count) % loop.Count].bridge;
                if (bridge == null) return false;
                for (int i = 0; i < steps; i++, count++)
                    if (count >= loop.Count || loop[(start + count) % loop.Count].bridge != bridge) return false;
                // Unequal insets may leave a straight segment between the end of this strip and the next.
                while (count < loop.Count && loop[(start + count) % loop.Count].bridge == null) count++;
                sides[side + 1] = count;
            }
            if (count != loop.Count) return false;

            int stride = steps + 1;
            var grid = new Vector3d[stride * stride];
            Vector3d c00 = CornerBoundaryVertex(loop, start, sides[0]);
            Vector3d c10 = CornerBoundaryVertex(loop, start, sides[1]);
            Vector3d c11 = CornerBoundaryVertex(loop, start, sides[2]);
            Vector3d c01 = CornerBoundaryVertex(loop, start, sides[3]);
            for (int v = 0; v <= steps; v++)
                for (int u = 0; u <= steps; u++)
                {
                    Vector3d bottom = FourGridAnchor(loop, start, sides, 0, u, steps);
                    Vector3d right = FourGridAnchor(loop, start, sides, 1, v, steps);
                    Vector3d top = FourGridAnchor(loop, start, sides, 2, steps - u, steps);
                    Vector3d left = FourGridAnchor(loop, start, sides, 3, steps - v, steps);
                    Vector3d point;
                    if (v == 0) point = bottom;
                    else if (v == steps) point = top;
                    else if (u == 0) point = left;
                    else if (u == steps) point = right;
                    else
                    {
                        double s = (double)u / steps, t = (double)v / steps;
                        Vector3d corners = (c00 * (1 - s) + c10 * s) * (1 - t) +
                            (c01 * (1 - s) + c11 * s) * t;
                        point = bottom * (1 - t) + top * t + left * (1 - s) + right * s - corners;
                    }
                    grid[u + v * stride] = point;
                }

            var cells = new List<Face>(steps * steps);
            List<TriangleFace> transitions = null;
            for (int v = 0; v < steps; v++)
                for (int u = 0; u < steps; u++)
                {
                    int i = u + v * stride;
                    Vector3d a = grid[i], b = grid[i + 1], c = grid[i + stride + 1], d = grid[i + stride];
                    bool bottom = u == steps - 1 && v == 0 && sides[1] - sides[0] > steps;
                    bool right = u == steps - 1 && v == steps - 1 && sides[2] - sides[1] > steps;
                    bool top = u == 0 && v == steps - 1 && sides[3] - sides[2] > steps;
                    bool left = u == 0 && v == 0 && sides[4] - sides[3] > steps;
                    if (!bottom && !right && !top && !left)
                    {
                        AddCornerGridQuad(loop, a, b, c, d, cells);
                        continue;
                    }

                    // Only the cell touching a connector needs extra vertices on its outer edge.
                    var outline = new List<Vector3d>(8) { a };
                    if (bottom) AppendGridConnector(outline, loop, start, sides, 0, steps);
                    outline.Add(b);
                    if (right) AppendGridConnector(outline, loop, start, sides, 1, steps);
                    outline.Add(c);
                    if (top) AppendGridConnector(outline, loop, start, sides, 2, steps);
                    outline.Add(d);
                    if (left) AppendGridConnector(outline, loop, start, sides, 3, steps);

                    JunctionBorder owner = loop[NearestBridgeBorder(loop, (a + b + c + d) * 0.25)];
                    var cell = new List<JunctionBorder>(outline.Count);
                    for (int j = 0; j < outline.Count; j++)
                        cell.Add(new JunctionBorder
                        {
                            a = outline[j], b = outline[(j + 1) % outline.Count],
                            color = owner.color, material = owner.material
                        });
                    List<TriangleFace> fill = TriangulateJunction(cell, false);
                    if (fill == null) return false;
                    transitions ??= new List<TriangleFace>();
                    transitions.AddRange(fill);
                }

            quads.AddRange(cells);
            if (transitions != null)
            {
                triangles ??= source.Triangles == null
                    ? new List<TriangleFace>() : new List<TriangleFace>(source.Triangles);
                triangles.AddRange(transitions);
            }
            return true;
        }

        /// <summary>Returns a strip sample or, at the end of a side, the start of the next strip.</summary>
        private static Vector3d FourGridAnchor(List<JunctionBorder> loop, int start, int[] sides,
            int side, int sample, int steps) =>
            CornerBoundaryVertex(loop, start, sample == steps ? sides[side + 1] : sides[side] + sample);

        /// <summary>Inserts the exact intermediate boundary points skipped by the final grid interval.</summary>
        private static void AppendGridConnector(List<Vector3d> outline, List<JunctionBorder> loop,
            int start, int[] sides, int side, int steps)
        {
            for (int i = sides[side] + steps; i < sides[side + 1]; i++)
                outline.Add(CornerBoundaryVertex(loop, start, i));
        }

        /// <summary>Fills three strip ends with three quad grids and, for an odd span count, a center triangle.</summary>
        private void FillThreeBridgeGrid(List<JunctionBorder> loop, int steps, List<Face> quads,
            ref List<TriangleFace> triangles)
        {
            if (loop.Count != steps * 3) return;
            int start = 0;
            while (start < loop.Count && loop[start].bridge == loop[(start + loop.Count - 1) % loop.Count].bridge)
                start++;
            if (start == loop.Count) return;
            for (int i = 0; i < loop.Count; i++)
                if (loop[(start + i) % loop.Count].bridge != loop[(start + i / steps * steps) % loop.Count].bridge)
                    return;

            if (steps == 1)
            {
                AddCornerGridTriangle(loop, CornerBoundaryVertex(loop, start, 0),
                    CornerBoundaryVertex(loop, start, 1), CornerBoundaryVertex(loop, start, 2), ref triangles);
                return;
            }

            int half = steps / 2, stride = half + 1;
            bool odd = (steps & 1) != 0;
            double middle = (double)half / steps;
            var axes = new[] { Vector3d.AxisX, Vector3d.AxisY, Vector3d.AxisZ };
            var centerWeights = new Vector3d[3];
            var centers = new Vector3d[3];
            Vector3d centroid = new Vector3d(1.0 / 3.0);
            for (int i = 0; i < 3; i++)
            {
                // Even spans share one central vertex. Odd spans reserve a triangle between the grids.
                centerWeights[i] = odd ? centroid + (axes[i] - centroid) / steps : centroid;
                centers[i] = SampleCornerGrid(loop, start, steps, centerWeights[i]);
            }

            var nextSpokes = new Vector3d[3][];
            var prevSpokes = new Vector3d[3][];
            for (int i = 0; i < 3; i++)
            {
                int next = (i + 1) % 3;
                Vector3d weights = axes[i] * (1 - middle) + axes[next] * middle;
                nextSpokes[i] = SampleCornerSpoke(loop, start, steps, i * steps + half,
                    weights, centerWeights[i], centers[i]);
                // Adjacent grids use the same spoke vertices when the side splits evenly.
                prevSpokes[next] = odd
                    ? SampleCornerSpoke(loop, start, steps, i * steps + steps - half,
                        axes[i] * middle + axes[next] * (1 - middle), centerWeights[next], centers[next])
                    : nextSpokes[i];
            }

            var grid = new Vector3d[stride * stride];
            for (int i = 0; i < 3; i++)
            {
                Vector3d corner = axes[i];
                Vector3d forward = corner * (1 - middle) + axes[(i + 1) % 3] * middle;
                Vector3d backward = corner * (1 - middle) + axes[(i + 2) % 3] * middle;
                for (int v = 0; v <= half; v++)
                    for (int u = 0; u <= half; u++)
                    {
                        Vector3d point;
                        if (v == 0) point = CornerBoundaryVertex(loop, start, i * steps + u);
                        else if (u == 0) point = CornerBoundaryVertex(loop, start, i * steps - v);
                        else if (u == half) point = nextSpokes[i][v];
                        else if (v == half) point = prevSpokes[i][u];
                        else
                        {
                            double s = (double)u / half, t = (double)v / half;
                            Vector3d weights = (corner * (1 - s) + forward * s) * (1 - t) +
                                (backward * (1 - s) + centerWeights[i] * s) * t;
                            point = SampleCornerGrid(loop, start, steps, weights);
                        }
                        grid[u + v * stride] = point;
                    }
                for (int v = 0; v < half; v++)
                    for (int u = 0; u < half; u++)
                    {
                        int k = u + v * stride;
                        AddCornerGridQuad(loop, grid[k], grid[k + 1], grid[k + stride + 1], grid[k + stride], quads);
                    }
            }

            if (!odd) return;
            // The single span between each pair of grids continues inward as a row of quads.
            for (int i = 0; i < 3; i++)
            {
                Vector3d[] left = nextSpokes[i], right = prevSpokes[(i + 1) % 3];
                for (int j = 0; j < half; j++)
                    AddCornerGridQuad(loop, left[j], right[j], right[j + 1], left[j + 1], quads);
            }
            AddCornerGridTriangle(loop, centers[0], centers[1], centers[2], ref triangles);
        }

        /// <summary>Samples one shared inner boundary from an exact strip vertex to a grid center.</summary>
        private static Vector3d[] SampleCornerSpoke(List<JunctionBorder> loop, int start, int steps,
            int boundaryIndex, Vector3d weights, Vector3d centerWeights, Vector3d center)
        {
            int half = steps / 2;
            var points = new Vector3d[half + 1];
            points[0] = CornerBoundaryVertex(loop, start, boundaryIndex);
            points[half] = center;
            for (int i = 1; i < half; i++)
                points[i] = SampleCornerGrid(loop, start, steps, weights + (centerWeights - weights) * ((double)i / half));
            return points;
        }

        /// <summary>Blends the three actual boundary curves over a triangular domain.</summary>
        private static Vector3d SampleCornerGrid(List<JunctionBorder> loop, int start, int steps, Vector3d weights)
        {
            Vector3d a = CornerBoundaryVertex(loop, start, 0);
            Vector3d b = CornerBoundaryVertex(loop, start, steps);
            Vector3d c = CornerBoundaryVertex(loop, start, 2 * steps);
            // Each correction vanishes at its endpoints and is exact on its own side.
            // Widths are already in the sampled curves, including each bridge's longitudinal setback.
            return a * weights.x + b * weights.y + c * weights.z +
                CornerProfileCorrection(loop, start, steps, 0, a, b, weights.x, weights.y) +
                CornerProfileCorrection(loop, start, steps, steps, b, c, weights.y, weights.z) +
                CornerProfileCorrection(loop, start, steps, 2 * steps, c, a, weights.z, weights.x);
        }

        /// <summary>Transfers the deviation of one bridge profile from its straight chord into the grid interior.</summary>
        private static Vector3d CornerProfileCorrection(List<JunctionBorder> loop, int start, int steps,
            int side, Vector3d a, Vector3d b, double first, double second)
        {
            if (first <= 0 || second <= 0) return Vector3d.Zero;
            double extent = first + second;
            double t = second / extent, sample = t * steps;
            int index = Math.Min((int)sample, steps - 1);
            Vector3d from = CornerBoundaryVertex(loop, start, side + index);
            Vector3d to = CornerBoundaryVertex(loop, start, side + index + 1);
            Vector3d curve = from + (to - from) * (sample - index);
            return (curve - (a + (b - a) * t)) * (extent * extent);
        }

        /// <summary>Reads a strip sample in loop order, with zero at the first inset face corner.</summary>
        private static Vector3d CornerBoundaryVertex(List<JunctionBorder> loop, int start, int index) =>
            loop[(start + index + loop.Count) % loop.Count].a;

        /// <summary>Emits one quad cell with appearance inherited from its nearest strip.</summary>
        private static void AddCornerGridQuad(List<JunctionBorder> loop, Vector3d a, Vector3d b, Vector3d c,
            Vector3d d, List<Face> quads)
        {
            JunctionBorder owner = loop[NearestBridgeBorder(loop, (a + b + c + d) * 0.25)];
#if MODIFIER_BEVEL_DEBUG_COLORS
            uint color = CornerPatchColor;
#else
            uint color = owner.color;
#endif
            quads.Add(new Face(a, b, c, d, color, owner.material));
        }

        /// <summary>Closes the center of an odd grid, or the whole corner at one span.</summary>
        private void AddCornerGridTriangle(List<JunctionBorder> loop, Vector3d a, Vector3d b, Vector3d c,
            ref List<TriangleFace> triangles)
        {
            JunctionBorder owner = loop[NearestBridgeBorder(loop, (a + b + c) / 3)];
#if MODIFIER_BEVEL_DEBUG_COLORS
            uint color = CornerPatchColor;
#else
            uint color = owner.color;
#endif
            triangles ??= source.Triangles == null
                ? new List<TriangleFace>() : new List<TriangleFace>(source.Triangles);
            triangles.Add(new TriangleFace(a, b, c, color, owner.material));
        }

        /*
         * TODO: PRESERVE MATERIAL TRANSITIONS THROUGH BEVEL JUNCTIONS.
         *
         * A junction can meet bridge spans and source faces with different material IDs.
         * The transition can enter on one side, cross the interior of the fill, and leave on
         * another side. The dark green bridge-to-cut patch exposes this when the seam on
         * the two source quads continues into the bridge. The material discontinuity exists
         * even when diagnostic colors are disabled.
         *
         * This triangulator receives only an ordered geometric loop. Its diagonals are
         * chosen for the shape, so a diagonal can cross the intended material seam. Picking
         * the longest adjoining bridge segment or the nearest segment gives each triangle
         * a reasonable owner, but cannot split a triangle that spans two materials.
         *
         * Carry the ordered material transitions from the surrounding faces and bridge
         * spans into the junction. Connect the matching transitions inside that surface fan
         * and constrain triangulation to those segments. Insert vertices where needed and
         * triangulate each material region separately. Reuse the strip and cut boundary
         * samples and split any neighboring side at a new seam endpoint so the patch stays
         * closed. Handle multiple transitions and keep edge-only voxel contacts separate.
         */
        /// <summary>Projects the boundary onto its average plane and clips the shortest valid ears while keeping the original 3D vertices.</summary>
        private static List<TriangleFace> TriangulateJunction(List<JunctionBorder> loop, bool followsCut)
        {
            var remaining = new List<int>(loop.Count);
            Vector3d origin = loop[0].a, normal = Vector3d.Zero;
            for (int i = 0; i < loop.Count; i++)
            {
                JunctionBorder edge = loop[i];
                normal += Vector3d.Cross(edge.a - origin, edge.b - origin);
                remaining.Add(i);
            }
            if (normal.LengthSquared < 1e-20) return null;
            normal /= normal.Length;
            // A crossed projected loop cannot be filled reliably by ear clipping.
            for (int i = 0; i < loop.Count; i++)
                for (int j = i + 2; j < loop.Count; j++)
                {
                    if (i == 0 && j == loop.Count - 1) continue;
                    JunctionBorder first = loop[i], second = loop[j];
                    if (ProjectedSide(first.a, first.b, second.a, normal) *
                        ProjectedSide(first.a, first.b, second.b, normal) < -1e-28 &&
                        ProjectedSide(second.a, second.b, first.a, normal) *
                        ProjectedSide(second.a, second.b, first.b, normal) < -1e-28) return null;
                }

            var result = new List<TriangleFace>(loop.Count - 2);
            while (remaining.Count > 3)
            {
                int ear = -1;
                double shortest = double.PositiveInfinity;
                for (int i = 0; i < remaining.Count; i++)
                {
                    int before = remaining[(i + remaining.Count - 1) % remaining.Count];
                    int current = remaining[i], after = remaining[(i + 1) % remaining.Count];
                    Vector3d a = loop[before].a, b = loop[current].a, c = loop[after].a;
                    double length = (c - a).LengthSquared;
                    if (length >= shortest || ProjectedSide(a, b, c, normal) <= 1e-14) continue;
                    bool clear = true;
                    foreach (int other in remaining)
                    {
                        if (other == before || other == current || other == after) continue;
                        Vector3d point = loop[other].a;
                        if (ProjectedSide(a, b, point, normal) >= -1e-14 &&
                            ProjectedSide(b, c, point, normal) >= -1e-14 &&
                            ProjectedSide(c, a, point, normal) >= -1e-14)
                        {
                            clear = false;
                            break;
                        }
                    }
                    if (!clear) continue;
                    ear = i;
                    shortest = length;
                }
                if (ear < 0) return null;
                AddJunctionTriangle(result, loop, remaining[(ear + remaining.Count - 1) % remaining.Count],
                    remaining[ear], remaining[(ear + 1) % remaining.Count], followsCut);
                remaining.RemoveAt(ear);
            }
            if (ProjectedSide(loop[remaining[0]].a, loop[remaining[1]].a, loop[remaining[2]].a, normal) <= 1e-14)
                return null;
            AddJunctionTriangle(result, loop, remaining[0], remaining[1], remaining[2], followsCut);
            return result;
        }

        /// <summary>Measures the signed area of a point relative to a directed edge in the projection plane.</summary>
        private static double ProjectedSide(Vector3d a, Vector3d b, Vector3d point, Vector3d normal) =>
            Vector3d.Dot(Vector3d.Cross(b - a, point - a), normal);

        /// <summary>Assigns the triangle the appearance of its supporting boundary and appends it to the fill.</summary>
        private static void AddJunctionTriangle(List<TriangleFace> result, List<JunctionBorder> loop,
            int a, int b, int c, bool followsCut)
        {
            int owner = 0;
            double longest = 0;
            ChooseBorderOwner(loop, a, b, followsCut, ref longest, ref owner);
            ChooseBorderOwner(loop, b, c, followsCut, ref longest, ref owner);
            ChooseBorderOwner(loop, c, a, followsCut, ref longest, ref owner);
            if (followsCut && longest == 0)
            {
                Vector3d center = (loop[a].a + loop[b].a + loop[c].a) / 3;
                owner = NearestBridgeBorder(loop, center);
            }
#if MODIFIER_BEVEL_DEBUG_COLORS
            uint color = followsCut ? CutJunctionPatchColor : CornerPatchColor;
#else
            uint color = loop[owner].color;
#endif
            result.Add(new TriangleFace(loop[a].a, loop[b].a, loop[c].a, color, loop[owner].material));
        }

        /// <summary>Prefers the longest original boundary edge that this triangle shares, filtering to bridge borders on a cut transition.</summary>
        private static void ChooseBorderOwner(List<JunctionBorder> loop, int start, int end,
            bool bridgeOnly, ref double longest, ref int owner)
        {
            if ((start + 1) % loop.Count != end || (bridgeOnly && !loop[start].followsBridge)) return;
            double length = (loop[start].b - loop[start].a).LengthSquared;
            if (length <= longest) return;
            longest = length;
            owner = start;
        }

        /// <summary>Finds the closest bridge segment for a cut-transition triangle that has no edge on a bridge.</summary>
        private static int NearestBridgeBorder(List<JunctionBorder> loop, Vector3d point)
        {
            int owner = 0;
            double nearest = double.PositiveInfinity;
            for (int i = 0; i < loop.Count; i++)
            {
                JunctionBorder border = loop[i];
                if (!border.followsBridge) continue;
                Vector3d direction = border.b - border.a;
                double t = Math.Max(0, Math.Min(1,
                    Vector3d.Dot(point - border.a, direction) / direction.LengthSquared));
                double distance = (point - (border.a + direction * t)).LengthSquared;
                if (distance >= nearest) continue;
                nearest = distance;
                owner = i;
            }
            return owner;
        }

        /// <summary>Chooses the source face for a profile span. With odd counts, the second face owns the extra span.</summary>
        private static SurfaceQuad BridgeSource(SurfaceEdge edge, int step, int steps) =>
            step <= steps / 2 ? edge.first : edge.second;

        /// <summary>Builds the strip between two moved sides and records any supported caps for later emission.</summary>
        private void BridgeMovedEdge(SurfaceEdge edge, int steps, List<Face> output, ref List<TriangleFace> triangles)
        {
            var (a0, b0, a1, b1) = GetBridgeRails(edge);
            Vector3d axis = (edge.b - edge.a).Normalized;
            if (axis.LengthSquared < 1e-20) return;

            BridgeProfile start = edge.profileA, end = edge.profileB;
            if (start.points == null) edge.capA = PrepareBridgeEnd(edge, true, axis, a0, a1);
            if (end.points == null) edge.capB = PrepareBridgeEnd(edge, false, axis, b0, b1);

            Vector3d a = start.points == null ? a0 : start.points[start.reversed ? steps : 0];
            Vector3d d = end.points == null ? b0 : end.points[end.reversed ? steps : 0];
            for (int step = 1; step <= steps; step++)
            {
                Vector3d b = start.points == null ? BridgePoint(edge.a, axis, a0, a1, step, steps)
                    : start.points[start.reversed ? steps - step : step];
                Vector3d c = end.points == null ? BridgePoint(edge.b, axis, b0, b1, step, steps)
                    : end.points[end.reversed ? steps - step : step];
                SurfaceQuad owner = BridgeSource(edge, step, steps);
#if MODIFIER_BEVEL_DEBUG_COLORS
                uint color = BridgeColor;
#else
                uint color = owner.color;
#endif
                uint material = owner.material;

                bool collapsedA = (a - b).LengthSquared < 1e-20;
                bool collapsedB = (c - d).LengthSquared < 1e-20;
                if (!collapsedA && !collapsedB)
                    output.Add(new Face(a, b, c, d, color, material));
                else if (collapsedA != collapsedB)
                {
                    triangles ??= source.Triangles == null
                        ? new List<TriangleFace>() : new List<TriangleFace>(source.Triangles);
                    triangles.Add(collapsedA ? new TriangleFace(a, c, d, color, material)
                        : new TriangleFace(a, b, c, color, material));
                }
                a = b;
                d = c;
            }
        }

        /// <summary>Finds the surrounding faces that can support a planar end patch and records their appearance and matching anchors.</summary>
        private BridgeEndPatch PrepareBridgeEnd(SurfaceEdge edge, bool atA, Vector3d axis,
            Vector3d firstPoint, Vector3d secondPoint)
        {
            int vertex = edge.first.vertexIds[(edge.firstSide + (atA ? 0 : 1)) & 3];
            int fan = EdgeFan(edge, vertex);
            SurfaceEdge firstTurnEdge = null, secondTurnEdge = null;
            foreach (var neighbor in incidentEdges[vertex])
            {
                if (neighbor != edge && HasBridge(neighbor) && EdgeFan(neighbor, vertex) == fan)
                {
                    if (firstTurnEdge == null) firstTurnEdge = neighbor;
                    else if (secondTurnEdge == null) secondTurnEdge = neighbor;
                    else return null;
                }
            }
            if (firstTurnEdge != null && (secondTurnEdge == null || !ShareBridgeFace(firstTurnEdge, secondTurnEdge)))
                return null;

            Vector3d origin = atA ? edge.a : edge.b;
            double orientation = Vector3d.Dot(axis, Vector3d.Cross(firstPoint - origin, secondPoint - origin));
            if (Math.Abs(orientation) < 1e-10) return null;
            // The cap winding points to the floor at an inside fold and to the underside at an overhang.
            Vector3d supportNormal = (atA ? -orientation : orientation) > 0 ? axis : -axis;
            var first = FindEndSupport(edge.first, edge.firstSide, vertex, origin, firstPoint, supportNormal);
            var second = FindEndSupport(edge.second, edge.secondSide, vertex, origin, secondPoint, supportNormal);
            if (first.quad == null || second.quad == null || first.quad == second.quad ||
                (!first.ownsPatch && !second.ownsPatch)) return null;
            if (first.ownsPatch && second.ownsPatch && (!first.insideFold || !second.insideFold))
                return null;
            // A single support can extend over a separate turn from either kind of crease.
            if (firstTurnEdge != null && (first.ownsPatch == second.ownsPatch ||
                !SameBridgePoint(firstPoint, OutlineAnchor(edge.first, edge.firstSide, vertex)) ||
                !SameBridgePoint(secondPoint, OutlineAnchor(edge.second, edge.secondSide, vertex))))
                return null;

            // The L follows the two original side edges and the already sampled bridge end.
            AddSideSupport(first.quad, first.side, vertex, firstPoint);
            AddSideSupport(second.quad, second.side, vertex, secondPoint);
            return new BridgeEndPatch
            {
                firstSource = first.ownsPatch ? first.quad : second.quad,
                secondSource = second.ownsPatch ? second.quad : first.quad
            };
        }

        /// <summary>Emits the caps for both ends after all adjacent junctions have registered support vertices.</summary>
        private void EmitBridgeEndPatches(SurfaceEdge edge, int steps, ref List<TriangleFace> triangles)
        {
            triangles ??= source.Triangles == null
                ? new List<TriangleFace>() : new List<TriangleFace>(source.Triangles);
            var (a0, b0, a1, b1) = GetBridgeRails(edge);
            Vector3d axis = (edge.b - edge.a).Normalized;
            if (edge.capA != null) EmitBridgeEndPatch(edge.capA, edge.a, axis, a0, a1, steps, true, triangles);
            if (edge.capB != null) EmitBridgeEndPatch(edge.capB, edge.b, axis, b0, b1, steps, false, triangles);
        }

        /// <summary>Fans the cap from the original corner and splits a triangle when a neighboring patch needs a vertex on a leg.</summary>
        private void EmitBridgeEndPatch(BridgeEndPatch patch, Vector3d origin, Vector3d axis,
            Vector3d first, Vector3d second, int steps, bool atA, List<TriangleFace> triangles)
        {
            Vector3d previous = first;
            for (int step = 1; step <= steps; step++)
            {
                Vector3d next = BridgePoint(origin, axis, first, second, step, steps);
                SurfaceQuad owner = step <= steps / 2 ? patch.firstSource : patch.secondSource;
                Vector3d? firstSupport = step == 1 ? patch.firstSupport : null;
                Vector3d? secondSupport = step == steps ? patch.secondSupport : null;
                if (firstSupport is Vector3d nearFirst)
                {
                    AddEndPatchTriangle(triangles, origin, nearFirst, secondSupport ?? next, atA, owner);
                    if (secondSupport is Vector3d nearSecond)
                        AddEndPatchTriangle(triangles, nearFirst, next, nearSecond, atA, owner);
                    AddEndPatchTriangle(triangles, nearFirst, previous, next, atA, owner);
                }
                else if (secondSupport is Vector3d nearEnd)
                {
                    AddEndPatchTriangle(triangles, origin, previous, nearEnd, atA, owner);
                    AddEndPatchTriangle(triangles, nearEnd, previous, next, atA, owner);
                }
                else
                    AddEndPatchTriangle(triangles, origin, previous, next, atA, owner);
                previous = next;
            }
        }

        /// <summary>Emits a cap triangle with winding opposite the strip along their common edge.</summary>
        private static void AddEndPatchTriangle(List<TriangleFace> triangles,
            Vector3d a, Vector3d b, Vector3d c, bool atA, SurfaceQuad owner)
        {
#if MODIFIER_BEVEL_DEBUG_COLORS
            uint color = EndPatchColor;
#else
            uint color = owner.color;
#endif
            triangles.Add(atA ? new TriangleFace(a, c, b, color, owner.material)
                : new TriangleFace(a, b, c, color, owner.material));
        }

        /// <summary>Finds an unbridged adjoining face that owns a cap or provides a coplanar continuation for its other leg.</summary>
        private static (SurfaceQuad quad, int side, bool ownsPatch, bool insideFold) FindEndSupport(
            SurfaceQuad face, int bridgeSide, int vertex, Vector3d origin, Vector3d anchor, Vector3d supportNormal)
        {
            int side = face.vertexIds[bridgeSide] == vertex ? (bridgeSide + 3) & 3 : (bridgeSide + 1) & 3;
            SurfaceEdge hinge = face.edges[side];
            if (hinge.nonManifold || HasBridge(hinge)) return default;
            SurfaceQuad neighbor = hinge.first == face ? hinge.second : hinge.first;
            if (neighbor == null || !ContainsEdge(new Edge(hinge.a, hinge.b), origin, anchor)) return default;

            bool ownsPatch = Vector3d.Dot(neighbor.normal, supportNormal) > 0.9999;
            bool continuesWall = Vector3d.Dot(neighbor.normal, face.normal) > 0.9999;
            if (!ownsPatch && !continuesWall) return default;
            return (neighbor, hinge.first == neighbor ? hinge.firstSide : hinge.secondSide,
                ownsPatch, ownsPatch && IsUnbeveledInsideFold(hinge));
        }

        /// <summary>Stores a shared position at one end of a face side so the adjoining patch does not create a T junction.</summary>
        private static void AddSideSupport(SurfaceQuad quad, int side, int vertex, Vector3d anchor)
        {
            quad.sideSupports ??= new Vector3d?[8];
            quad.sideSupports[side * 2 + (quad.vertexIds[side] == vertex ? 0 : 1)] = anchor;
        }

        /// <summary>Inserts support positions in winding order on the actual outline after all cuts have been applied.</summary>
        private static void InsertSideSupports(SurfaceQuad quad, List<Vector3d> outline,
            int[] cornerStart, int[] cornerEnd)
        {
            for (int side = 3; side >= 0; side--)
            {
                int end = cornerEnd[side];
                Vector3d a = outline[end], b = outline[cornerStart[(side + 1) & 3]];
                Vector3d? first = quad.sideSupports[side * 2], second = quad.sideSupports[side * 2 + 1];
                if (first.HasValue && second.HasValue &&
                    Vector3d.Dot(first.Value - second.Value, b - a) > 0)
                    (first, second) = (second, first);
                if (second is Vector3d last && IsInteriorSupport(a, b, last)) outline.Insert(end + 1, last);
                if (first is Vector3d next && (!second.HasValue || !SameBridgePoint(next, second.Value)) &&
                    IsInteriorSupport(a, b, next)) outline.Insert(end + 1, next);
            }
        }

        /// <summary>Checks that a support point lies strictly between the endpoints of the side being split.</summary>
        private static bool IsInteriorSupport(Vector3d a, Vector3d b, Vector3d point) =>
            !SameBridgePoint(a, point) && !SameBridgePoint(b, point) && ContainsEdge(new Edge(a, b), point, point);

        /// <summary>Samples a profile across the original edge while interpolating its longitudinal position between the two rail anchors.</summary>
        private Vector3d BridgePoint(Vector3d origin, Vector3d axis, Vector3d first, Vector3d second,
            int step, int steps)
        {
            if (step == steps) return second;
            Vector3d point = ProfilePoint(origin, first - origin, second - origin, step, steps);
            // Use the rounded profile across the edge and stay between the rail ends along it.
            Vector3d rail = first + (second - first) * ((double)step / steps);
            return point + axis * Vector3d.Dot(rail - point, axis);
        }

        /// <summary>Samples the L profile or the rounded profile between two offsets.</summary>
        private Vector3d ProfilePoint(Vector3d origin, Vector3d first, Vector3d second,
            int step, int steps)
        {
            if (step == 0) return origin + first;
            if (step == steps) return origin + second;
            if (profileRoundness <= 0)
            {
                int turn = steps / 2;
                return step <= turn
                    ? origin + first + second * ((double)step / turn)
                    : origin + second + first * ((double)(steps - step) / (steps - turn));
            }
            if (profileRoundness >= 1) return origin;

            double angle = Math.PI * 0.5 * step / steps;
            // Weight the two offsets with a superellipse. Roundness 0.5 gives a quarter circle at equal widths.
            double exponent = (1 - profileRoundness) / profileRoundness;
            double firstWeight = 1 - Math.Pow(Math.Sin(angle), exponent);
            double secondWeight = 1 - Math.Pow(Math.Cos(angle), exponent);
            return origin + first * firstWeight + second * secondWeight;
        }

        /// <summary>Appends an inward cut using the strip profile and skipping repeated samples.</summary>
        private void AppendInnerProfile(List<Vector3d> outline, Vector3d origin,
            Vector3d first, Vector3d second)
        {
            int steps = ProfileSteps();
            for (int step = 0; step <= steps; step++)
                AddDistinctPoint(outline, ProfilePoint(origin, first, second, step, steps));
        }

        /// <summary>Clips the corner-centered profile to the portion still inside the inset face.</summary>
        private bool TryGetOuterAngles(Vector3d origin, Vector3d moved,
            Vector3d outgoing, Vector3d incoming, double uMax, double vMax,
            out double startAngle, out double endAngle)
        {
            startAngle = endAngle = 0;
            double baseU = Vector3d.Dot(moved - origin, outgoing);
            double baseV = Vector3d.Dot(moved - origin, incoming);
            if (uMax <= 0 || vMax <= 0 || baseU >= uMax || baseV >= vMax)
                return false;

            double exponent = profileRoundness / (1 - profileRoundness);
            double uRatio = Math.Max(0, baseU / uMax);
            double vRatio = Math.Max(0, baseV / vMax);
            startAngle = Math.Asin(Math.Pow(uRatio, 1 / exponent));
            endAngle = Math.Acos(Math.Pow(vRatio, 1 / exponent));
            return startAngle + 1e-12 < endAngle;
        }

        /// <summary>Samples a corner-centered arc between its two clipped intersections with the face sides.</summary>
        private void AppendOuterProfile(List<Vector3d> outline, Vector3d origin,
            Vector3d outgoing, Vector3d incoming, double uMax, double vMax,
            double startAngle, double endAngle)
        {
            int steps = ProfileSteps();
            double exponent = profileRoundness / (1 - profileRoundness);
            for (int step = 0; step <= steps; step++)
            {
                double angle = startAngle + (endAngle - startAngle) * step / steps;
                Vector3d point = origin +
                    outgoing * (uMax * Math.Pow(Math.Sin(angle), exponent)) +
                    incoming * (vMax * Math.Pow(Math.Cos(angle), exponent));
                AddDistinctPoint(outline, point);
            }
        }

        /// <summary>Returns the sample count used across cuts and strips, with a minimum of two for the L shape.</summary>
        private int ProfileSteps() => Math.Max(profileRoundness <= 0 ? 2 : 1, bevelSegments);

        /// <summary>Appends a point only when it differs from the previous sample.</summary>
        private static void AddDistinctPoint(List<Vector3d> outline, Vector3d point)
        {
            if (outline.Count == 0 ||
                (outline[outline.Count - 1] - point).LengthSquared > 1e-20)
                outline.Add(point);
        }

#if MODIFIER_BEVEL_DEBUG_COLORS
        /// <summary>Checks whether a diagnostic face should be colored as an offset rather than shown in its original color.</summary>
        private static bool QuadWasMoved(SurfaceQuad quad, Vector3d[] moved)
        {
            for (int i = 0; i < 4; i++)
                if ((moved[i] - quad.points[i]).LengthSquared > 1e-20)
                    return true;
            return false;
        }
#endif
    }
}
