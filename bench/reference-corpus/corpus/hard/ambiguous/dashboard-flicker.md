---
title: Flicker incident review
category: ops
tags: [incident, dashboard, flicker]
---
The dashboard flicker was traced to a refresh of the metrics widget, not to the
polling interval. The widget re-rendered on every data frame instead of on every
data set, so the flicker rate scaled with the frame rate rather than with the
refresh rate. Users on a thirty hertz display saw it twice as often as users on
sixty. The fix was one guard clause.
