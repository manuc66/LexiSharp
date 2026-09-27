---
title: Widget render loop
category: ops
tags: [dashboard, rendering, performance]
---
The render loop repaints whenever the data set changes. Repaints triggered by
anything else, such as an animation frame, are a bug in the widget rather than
in the loop. Profiling a flicker usually means counting repaints per data set.
