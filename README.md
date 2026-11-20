# Pi-Dev WorldFlow Designer

Pi-Dev WorldFlow Designer is a Unity tool for level designers, game designers, and
prototypers. Paint some terrain with auto prefab placement, draw some spline graph roads,
sketch a floor plan, build some walls and structures, all in in the same workspace. 
Then let our procdeural node-graph system build the things for you. 

And most of this is also non-destructive, though, don't expect it 
updating your entire world if you misplanned a road.

Pi-Dev WorldFlow Designer is in development and is not yet available. 

## WorldFlow Canvas

Canvas is the min 3D-volume place where you sketch, sculpt, and build. 
Place blocks, 3D tilemaps, terrain, and lighting into separate layers in Unity's hierarchy.
Canvas's layer system is GameObject based, and each Canvas object is intended to represent one level design feature. 
You can nest canvases and save canvases as assets and expose parameters, like in Blender's geometry nodes.

- **Set up your workflow.** Define reusable assets for your worlds - 3D tile maps, floors, ceilings, wall styles, 
  spline graph objects, and set rules for how they fit together by basic settings or powerful node graphs.

- **Paint the world.** Sculpt your voxel terrains, dig caves, 
  either freehand, or by using non-destructive spline workflow, or proxy meshes.  

- **Sketch walls and floors.** Use CAD-style drawing tools and CSG to lay out rooms,
  corridors, and larger structures, then integrate windows into walls and holes / features into floors, e.g. staircases. 
  Duplicate your floor sketches to build your entire building and override what's necessary per element,
  then define both exterior and interior "partitions" to use them with our world streaming solution.

- **Build along curves.** Draw connected paths and generate shapes such as
  pipes or trim along them. Round edges, join shapes, or cut openings as you
  work. Then define your roars, pipe systems or even procedural vines.

## WorldFlow Terrain

- **Heightmap terrain** replaces Unity's default terrain for areas that need
  more surface detail. Standard Unity terrain is still recommended to cover distant scenery
  and transition into WorldFlow terrain up close.
- **Voxel terrains** uses Marching Cubes for sculpted hills, caves, and
  overhangs, or Dual Contouring to combine sharp cliffs and corners
  with smooth ground in the same surface. 
- **Spline based zonprefab placement.** Define an area that defines vegeration, or fences, or road boundaries. 

And all our terrain support hardware-level GPU tessellation and heightmap displacement for the finest details. 

## WorldFlow logic nodes system

At its core, WorldFlow uses a logic node graphs system to define how the world 
and its features are built and modified. The rules in a graph can tell WorldFlow 
how to join fence panels at a corner, reshape the ground beneath a road, 
or cut an opening where a spline meets a wall. 
Single graph can define behaviors for all WorldFlow Canvas features,
but are the core of spline objects, floors, walls and volume objects,
but each graph is defined with single primary usage in mind.
And most of there prefabs can be customized after built, because graph objects 
expose ways to override its reslts and manually attach objects.

### Build along splines

To build a fence, define WorldFlow Spline graph, place a wood plank meshes and a pole mesh.
Define how panels and posts are placed along straight runs, following the spline,
and what happens at sharp turns, T-junctions, and four-way intersections.
You can also supply a profile for geometry that should follow the curve,
and you can mix this with Unity's Spline package basic extruder.
Your graph can create other splines and instantiate other spline objects, 
so you can define hierarchy of asset definitions. 

Spline rules can describe what happens when the path runs into another
feature. For example, when a path reaches a wall, the graph can cut an opening
or place the piece you supplied for that intersection, or just let it clip through.

Graphs can have priority, which is also overridable per instance.
So you can define a single whole railway tunnel logic with single spline asset

### Work across Canvas layers

A volume graph can define what happens inside a marked area. It can add
blocks to a 3D tilemap, place prefabs, or use CSG to cut or combine shapes.
The graph defines the operation. The designer draws the volume where it
should apply, and volumes are project assets too.

Good example is a volume that builds tiles in a tilemap, walls, and as well erases things from terrain.
A volume can define walls in its sides, floors and ceilings to become a room prototype. 

Or just setup a base volume that prepares for building.

### Expose settings and keep the source editable

Expose settings such as fence panel spacing or road shoulder width so
designers can adjust them in the inspector where they use the graph. The same graph can serve
several features with different settings.

Editing is designed to be non-destructive. Keep the spline, volume, or other
source in Canvas so you can change the layout and regenerate the result.

## Lighting and streaming

### WorldFlow Luminator

Luminator uses real-time voxel-based global illumination. Light bounces
between surfaces, so a bright room can spill light into a shaded hallway.
Use lights with custom shapes or paint directly into the lighting volume
with brushes on a Canvas lighting layer. Every WorldFlow shader supports Luminator, 
and WorldFlow includes an expanded URP shaders for your non-WorldFlow objects. 
Materials with emissive maps also emit light into the lighting volume.

So your procedural dungeon genreators will support dynamic GI during runtime,
and you can opt-out of baking everything, though Luminator is optional.

### WorldFlow SpatialDB

SpatialDB is our data storage engine for Canvas objects and features, by location.

When you lay out a building in Canvas, you can keep its exterior and interior
in separate partitions. The same approach works for a street block, a sewer
network, or a stretch of terrain. 

### WorldFlow Streamer

WorldFlow Streamer is responsible to load and unload SpatialDB partitions.

Use a logic graph, common distance based management, or triggers to set the rules 
for loading and unloading those areas during play. 
A building's exterior can stay visible from the street, and when you approach the door, floors load on demand.
And if you just entered the city dungeons, the entire overworld can be unloaded to save performance.

