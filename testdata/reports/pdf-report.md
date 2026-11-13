# DgnKit vs official DOT plot PDFs

- **Text recall**: share of words on the official PDF page (text layer) that DgnKit extracted from the DGN (text, text nodes, tags, references).
- **Ink F1**: raster agreement between the official page and DgnKit's own PDF export at 40 dpi after aligning drawing extents (2 px tolerance). Overlay PNGs: blue = official only, red = DgnKit only, dark = both.

| item | dgn | model | pdf page | pdf words | text recall | text precision | ink recall | ink precision | ink F1 | note |
|---|---|---|---|---|---|---|---|---|---|---|
| fdot-000-510 | 000-510-1of2.dgn | Border Sheet | 1/2 | 182 | 94.0% | 97.2% | 81.4% | 99.4% | 89.5% | text-registered (84 anchors) |
| fdot-000-510 | 000-510-2of2.dgn | Border Sheet | 2/2 | 107 | 84.1% | 94.7% | 79.8% | 99.1% | 88.4% | text-registered (43 anchors) |
| fdot-102-600 | 102-600-10of11.dgn | Border Sheet | 10/11 | 207 | 95.7% | 94.7% | 77.3% | 98.8% | 86.7% | text-registered (134 anchors) |
| fdot-102-600 | 102-600-11of11.dgn | Border Sheet | 11/11 | 83 | 90.4% | 96.2% | 66.4% | 95.5% | 78.4% | text-registered (31 anchors) |
| fdot-102-600 | 102-600-1of11.dgn | Border Sheet | 1/11 | 204 | 97.1% | 98.5% | 78.1% | 99.7% | 87.6% | text-registered (136 anchors) |
| fdot-102-600 | 102-600-2of11.dgn | Border Sheet | 2/11 | 410 | 98.0% | 98.8% | 81.3% | 99.6% | 89.5% | text-registered (237 anchors) |
| fdot-102-600 | 102-600-3of11.dgn | Border Sheet | 3/11 | 332 | 94.9% | 96.9% | 78.6% | 98.2% | 87.3% | text-registered (240 anchors) |
| fdot-102-600 | 102-600-4of11.dgn | Border Sheet | 4/11 | 436 | 98.4% | 99.1% | 78.5% | 99.2% | 87.6% | text-registered (245 anchors) |
| fdot-102-600 | 102-600-5of11.dgn | Border Sheet | 5/11 | 248 | 98.8% | 95.3% | 99.0% | 99.7% | 99.3% | text-registered (185 anchors) |
| fdot-102-600 | 102-600-6of11.dgn | Border Sheet | 6/11 | 292 | 98.3% | 90.8% | 83.0% | 99.8% | 90.6% | text-registered (215 anchors) |
| fdot-102-600 | 102-600-7of11.dgn | Border Sheet | 7/11 | 216 | 91.7% | 98.0% | 70.6% | 99.1% | 82.5% | text-registered (130 anchors) |
| fdot-102-600 | 102-600-8of11.dgn | Border Sheet | 8/11 | 204 | 86.8% | 94.7% | 70.7% | 99.4% | 82.6% | text-registered (101 anchors) |
| fdot-102-600 | 102-600-9of11.dgn | Border Sheet | 9/11 | 247 | 91.5% | 95.4% | 75.5% | 98.5% | 85.5% | text-registered (40 anchors) |
| fdot-110-100 | 110-100-1of1.dgn | Border Sheet | 1/1 | 188 | 96.3% | 97.3% | 73.4% | 99.8% | 84.6% | text-registered (50 anchors) |
| njdot-traffic | Traffic Construction Details DGN file set__102_IndexForTrafficControlDetails.dgn | Default | 1/1 | 84 | 100.0% | 84.0% | 96.8% | 92.2% | 94.4% | text-registered (42 anchors) |
| njdot-traffic | Traffic Construction Details DGN file set__103_TCD-1LegendAndGeneralNotes.dgn | Default | 1/1 | 430 | 100.0% | 94.9% | 90.0% | 92.7% | 91.3% | text-registered (119 anchors) |
| njdot-traffic | Traffic Construction Details DGN file set__104_TCD-2ConstructionBarrier.dgn | Default | 1/1 | 210 | 100.0% | 93.3% | 94.5% | 93.8% | 94.1% | text-registered (119 anchors) |
| njdot-traffic | Traffic Construction Details DGN file set__105_TCD-3TwoLanes,Undivided,LaneAndShoulderClosing.dgn | Default | 1/1 | 68 | 100.0% | 82.9% | 93.2% | 89.2% | 91.1% | text-registered (35 anchors) |
| njdot-traffic | Traffic Construction Details DGN file set__106_TCD-4TwoLanes,Undivided,LaneAndShoulderClosing.dgn | Default | 1/1 | 74 | 97.3% | 84.7% | 92.3% | 86.9% | 89.5% | text-registered (35 anchors) |
| njdot-traffic | Traffic Construction Details DGN file set__107_TCD-5Two Lanes,Undivided,Intersection.dgn | Default | 1/1 | 62 | 100.0% | 82.7% | 94.1% | 92.2% | 93.2% | text-registered (35 anchors) |
| njdot-traffic | Traffic Construction Details DGN file set__108_TCD-6Two Lanes,Undivided,Intersection.dgn | Default | 1/1 | 61 | 100.0% | 81.3% | 92.5% | 89.7% | 91.1% | text-registered (33 anchors) |
| njdot-traffic | Traffic Construction Details DGN file set__109_TCD-7Two Lanes,Undivided,Intersection.dgn | Default | 1/1 | 63 | 100.0% | 82.9% | 93.1% | 90.4% | 91.7% | text-registered (37 anchors) |
| njdot-traffic | Traffic Construction Details DGN file set__110_TCD-8FourLanes,Undivided,LaneAndShoulderClosing.dgn | Default | 1/1 | 66 | 100.0% | 83.5% | 93.8% | 88.4% | 91.0% | text-registered (38 anchors) |
| njdot-traffic | Traffic Construction Details DGN file set__111_TCD-9Four Lanes,Undivided,LeftLaneClosing.dgn | Default | 1/1 | 64 | 100.0% | 83.1% | 91.3% | 86.8% | 89.0% | text-registered (34 anchors) |
| njdot-traffic | Traffic Construction Details DGN file set__112_TCD-10FourLanes,Undivided,2LanesAndShoulderClosing.dgn | Default | 1/1 | 71 | 100.0% | 82.6% | 96.0% | 89.5% | 92.6% | text-registered (34 anchors) |
| njdot-traffic | Traffic Construction Details DGN file set__113_TCD-11FourLanes,Undivided,Intersection.dgn | Default | 1/1 | 63 | 100.0% | 85.1% | 93.0% | 87.0% | 89.9% | text-registered (45 anchors) |
| njdot-traffic | Traffic Construction Details DGN file set__114_TCD-12FourLanes,Undivided,Intersection.dgn | Default | 1/1 | 76 | 84.2% | 83.1% | 93.9% | 90.2% | 92.0% | text-registered (33 anchors) |
| njdot-traffic | Traffic Construction Details DGN file set__115_TCD-13FourLanes,Undivided,Intersection.dgn | Default | 1/1 | 65 | 100.0% | 84.4% | 93.1% | 86.4% | 89.6% | text-registered (32 anchors) |
| njdot-traffic | Traffic Construction Details DGN file set__116_TCD-14_4&6Lanes,Divided,RightLanAndShoulderClosing.dgn | Default | 1/1 | 90 | 88.9% | 87.9% | 91.7% | 90.2% | 90.9% | text-registered (30 anchors) |
| njdot-traffic | Traffic Construction Details DGN file set__117_TCD-15_4&6Lanes,Divided,LeftLaneClosing.dgn | Default | 1/1 | 85 | 100.0% | 88.5% | 91.4% | 83.4% | 87.2% | text-registered (41 anchors) |
| njdot-traffic | Traffic Construction Details DGN file set__118_TCD-16SixLanes,Divided,TwoLanesClosing.dgn | Default | 1/1 | 94 | 90.4% | 88.5% | 92.2% | 89.5% | 90.8% | text-registered (31 anchors) |
| njdot-traffic | Traffic Construction Details DGN file set__119_TCD-17_6Lanes,Divided,CenterLaneClosure.dgn | Default | 1/1 | 116 | 100.0% | 90.6% | 84.9% | 81.7% | 83.3% | text-registered (19 anchors) |
| njdot-traffic | Traffic Construction Details DGN file set__120_TCD-18Divided,ExitRampConstruction(Left&Right).dgn | Default | 1/1 | 54 | 100.0% | 81.8% | 95.1% | 91.5% | 93.2% | text-registered (19 anchors) |
| njdot-traffic | Traffic Construction Details DGN file set__121_TCD-19Divided,ExitRampConstruction(Left&Right)WithDecelerationLane.dgn | Default | 1/1 | 59 | 98.3% | 82.9% | 96.9% | 92.3% | 94.6% | text-registered (19 anchors) |
| njdot-traffic | Traffic Construction Details DGN file set__122_TCD-20Divided,EntranceRampConstruction(Left&Right).dgn | Default | 1/1 | 60 | 100.0% | 75.0% | 97.7% | 92.5% | 95.0% | text-registered (36 anchors) |
| njdot-traffic | Traffic Construction Details DGN file set__123_TCD-21Divided,EntranceRampConstruction(Left&Right)WithAccelerationLane.dgn | Default | 1/1 | 48 | 97.9% | 73.4% | 97.2% | 90.9% | 94.0% | text-registered (23 anchors) |
| njdot-traffic | Traffic Construction Details DGN file set__124_TCD-22Multi-LaneRoadMovingOperation.dgn | Default | 1/1 | 51 | 100.0% | 79.7% | 95.1% | 83.6% | 89.0% | text-registered (47 anchors) |
| txdot-BB-AFL-24 | BB-AFL-24.dgn | BB-AFL-24 | 1/1 | 0 | 0.0% | 0.0% | 99.7% | 95.1% | 97.3% | official PDF has no text layer (text plotted as vectors) |
| txdot-cccg21 | cccg21.dgn | Default | 1/1 | 188 | 100.0% | 83.9% | 78.6% | 92.1% | 84.8% | text-registered (57 anchors) |
| txdot-CD-RAC-20 | CD-RAC-20.dgn | RAC_1 [Sheet] | 1/2 | 209 | 99.5% | 80.0% | 99.8% | 97.0% | 98.4% | text-registered (118 anchors) |
| txdot-crcp117 | crcp117.dgn | Default | 0/2 | 0 | 0.0% | 0.0% | – | – | – | official PDF has no text layer (text plotted as vectors) |
| txdot-csb104 | CSB104.DGN | Default | 1/1 | 0 | 0.0% | 0.0% | 77.8% | 85.0% | 81.2% | official PDF has no text layer (text plotted as vectors) |
| txdot-ec109 | ec109.dgn | Default | 1/1 | 0 | 0.0% | 0.0% | 79.7% | 88.4% | 83.9% | official PDF has no text layer (text plotted as vectors) |
| txdot-ec293 | ec293.dgn | Default | 1/1 | 0 | 0.0% | 0.0% | 84.3% | 91.3% | 87.7% | official PDF has no text layer (text plotted as vectors) |
| txdot-ed10-26 | ed10-26.dgn | ED10-26_1 [Sheet] | 1/1 | 317 | 99.4% | 89.5% | 99.9% | 99.8% | 99.9% | text-registered (238 anchors) |
| txdot-mb05-1 | MB05(1).DGN | Default | 0/3 | 0 | 0.0% | 0.0% | – | – | – | official PDF has no text layer (text plotted as vectors) |
| txdot-mb96 | MB96.DGN | Default | 0/3 | 0 | 0.0% | 0.0% | – | – | – | official PDF has no text layer (text plotted as vectors) |
| txdot-NOVUS10026 | NOVUS10026.dgn | CATGR17_1 [Sheet] | 1/1 | 0 | 0.0% | 0.0% | 13.1% | 77.6% | 22.4% | official PDF has no text layer (text plotted as vectors) |
| txdot-pm3-12 | pm3-12.dgn | Default | 1/1 | 0 | 0.0% | 0.0% | 90.9% | 96.6% | 93.7% | official PDF has no text layer (text plotted as vectors) |
| txdot-RSE-1-21 | RSE(1)-21.dgn | Default | 1/1 | 209 | 98.1% | 86.1% | 85.4% | 96.1% | 90.5% | text-registered (62 anchors) |
| txdot-SOLS-24 | SOLS-24.dgn | Default | 0/3 | 0 | 0.0% | 0.0% | – | – | – | official PDF has no text layer (text plotted as vectors) |
| txdot-tcp-1-4-26 | tcp(1-4)-26.dgn | TCP(1-4)-26_1 [Sheet] | 1/1 | 245 | 99.2% | 86.5% | 100.0% | 99.9% | 99.9% | text-registered (227 anchors) |
| txdot-trb04-1 | TRB04(1).DGN | Default | 1/1 | 0 | 0.0% | 0.0% | 19.9% | 43.2% | 27.3% | official PDF has no text layer (text plotted as vectors) |
| txdot-wf194 | WF194.DGN | Default | 1/1 | 0 | 0.0% | 0.0% | 80.2% | 90.4% | 85.0% | official PDF has no text layer (text plotted as vectors) |
| wsdot-XE3649 | SR 24 & 240 Paver_PS_RS.dgn | Default | 4/14 | 120 | 88.3% | 44.7% | 90.8% | 77.7% | 83.7% | text-registered (7 anchors) |
| wsdot-XE3649 | SR 24 & SR 240 Paver_PS_EU.dgn | Default | 3/14 | 70 | 84.3% | 95.2% | 22.0% | 36.3% | 27.4% |  |
| wsdot-XE3649 | SR 24 & SR240 Paver_PS_VM.dgn | Default | 3/14 | 70 | 72.9% | 39.5% | 75.0% | 35.0% | 47.8% | text-registered (26 anchors) |
| wsdot-XE3649 | SR24 & SR240 PAVER_PS_TC.dgn | Default | 14/14 | 162 | 93.2% | 36.0% | 70.3% | 70.2% | 70.3% | text-registered (34 anchors) |
| wsdot-XE3691 | XL7087_PS GR1.dgn | guardrail 4 | 4/9 | 98 | 92.9% | 85.8% | 96.2% | 99.3% | 97.7% | text-registered (71 anchors) |
| wsdot-XE3691 | XL7087_PS_CT.dgn | Untitled Sheet | 1/9 | 125 | 47.2% | 66.3% | 93.9% | 98.4% | 96.1% | text-registered (43 anchors) |
| wsdot-XE3691 | XL7087_PS_QT.dgn | Quantity Tabs | 3/9 | 80 | 92.5% | 93.7% | 94.7% | 97.4% | 96.0% | text-registered (81 anchors) |
| wsdot-XE3691 | XL7087_PS_RS1.dgn | roadway sections | 2/9 | 130 | 94.6% | 95.3% | 96.5% | 98.5% | 97.4% | text-registered (103 anchors) |
| wsdot-XE3691 | XL7087_PS_TC1.dgn | TC1 | 7/9 | 122 | 94.3% | 85.2% | 85.4% | 98.5% | 91.5% | text-registered (62 anchors) |
| wsdot-XE3691 | XL7087_PS_TC2.dgn | traffic control plan 3 | 8/9 | 127 | 85.8% | 92.4% | 79.3% | 98.7% | 87.9% | text-registered (67 anchors) |
| wsdot-XE3691 | XL7087_PS_TC3.dgn | traffic control plan | 9/9 | 102 | 88.2% | 90.9% | 80.3% | 98.5% | 88.5% | text-registered (61 anchors) |
| wsdot-XE3691 | XL7087_PS_TCR.dgn | TCR | 6/9 | 239 | 97.5% | 95.9% | 96.6% | 98.4% | 97.5% | text-registered (149 anchors) |
| wsdot-XE3691 | XL7087_PS_VM.dgn | Chumstick Detour Route [Sheet] | 3/9 | 80 | 57.5% | 13.4% | 77.7% | 38.2% | 51.3% | text-registered (16 anchors) |
| wsdot-XE3736 | 01_XL7089_PS_IV.dgn | Stevens Pass Flood Repair VM | 5/12 | 54 | 87.0% | 23.2% | 73.8% | 42.6% | 54.0% | text-registered (19 anchors) |
| wsdot-XE3736 | 04_XL7089_PS_CT1.dgn | Untitled Sheet | 1/12 | 97 | 89.7% | 95.6% | 94.1% | 98.6% | 96.3% | text-registered (36 anchors) |
| wsdot-XE3736 | 05_XL7089_PS_SP1.dgn | Untitled Sheet | 2/12 | 143 | 88.1% | 95.5% | 93.1% | 98.4% | 95.7% | text-registered (84 anchors) |
| wsdot-XE3736 | 06_XL7089_PS_RS1.dgn | RS1 | 3/12 | 140 | 92.9% | 94.2% | 89.8% | 98.8% | 94.1% | text-registered (92 anchors) |
| wsdot-XE3736 | 07_XL7089_PS_RS2.dgn | RS2 | 4/12 | 139 | 92.1% | 93.4% | 96.5% | 99.0% | 97.7% | text-registered (109 anchors) |
| wsdot-XE3736 | 08_XL7089_PS_QT1.dgn | QT1 | 5/12 | 54 | 88.9% | 92.3% | 47.0% | 77.6% | 58.6% | text-registered (23 anchors) |
| wsdot-XE3736 | 09_XL7089_PS_ DD1.dgn | RS1 | 6/12 | 78 | 91.0% | 85.5% | 94.0% | 98.6% | 96.2% | text-registered (37 anchors) |
| wsdot-XE3736 | 10_XL7089_PS_PD1.dgn | WSDOT_11x17_PSE Index Sheet Seed | 5/12 | 54 | 81.5% | 58.7% | 76.1% | 34.7% | 47.7% | text-registered (19 anchors) |
| wsdot-XE3736 | 11_XL7089_PS_CS1.dgn | CS1 - CS1 | 8/12 | 147 | 85.7% | 65.6% | 88.0% | 89.5% | 88.7% | text-registered (114 anchors) |
| wsdot-XE3736 | 12_XL7089_PS_TC1.dgn | Shoulder Closure-1 [Sheet] | 9/12 | 254 | 94.5% | 92.0% | 92.2% | 87.4% | 89.7% | text-registered (158 anchors) |
| wsdot-XE3736 | 13_XL7089_PS_TC2.dgn | Shoulder Closure-1 [Sheet] | 10/12 | 273 | 94.9% | 69.3% | 92.9% | 98.8% | 95.8% | text-registered (163 anchors) |
| wsdot-XE3736 | 14_XL7089_PS_TC3.dgn | Shoulder Closure-1 [Sheet] | 11/12 | 214 | 96.3% | 86.9% | 92.5% | 79.6% | 85.6% | text-registered (103 anchors) |
| wsdot-XE3736 | 15_XL7089_PS_TC4.dgn | Shoulder Closure-1 [Sheet] | 12/12 | 205 | 96.6% | 65.8% | 89.2% | 72.6% | 80.0% | text-registered (80 anchors) |
| wsdot-XE3736 | XL7089_CF_CS.dgn | Default | 2/12 | 143 | 4.2% | 9.7% | 1.4% | 18.3% | 2.5% |  |
| wsdot-XE3771 | 316251H_BP_VM.dgn | Default | 2/9 | 126 | 5.6% | 41.2% | 26.0% | 41.7% | 32.0% |  |
| wsdot-XE3771 | 316251H_PS_VM.dgn | Vicinity Map | 2/9 | 126 | 86.5% | 75.7% | 82.7% | 89.6% | 86.1% | text-registered (69 anchors) |
| wsdot-XE3771 | XL6985_PD_WZTC_TC00.dgn | CLASS A | 7/9 | 135 | 88.9% | 93.8% | 89.0% | 99.6% | 94.0% | text-registered (41 anchors) |
| wsdot-XE3771 | XL6985_PS_CT01.dgn | Untitled Sheet | 4/9 | 94 | 90.4% | 95.5% | 94.2% | 98.0% | 96.1% | text-registered (36 anchors) |
| wsdot-XE3771 | XL6985_PS_IN01.dgn | Untitled Sheet | 1/9 | 74 | 91.9% | 94.4% | 44.4% | 88.6% | 59.1% | text-registered (32 anchors) |
| wsdot-XE3771 | XL6985_PS_QT01.dgn | Untitled Sheet | 5/9 | 56 | 89.3% | 92.6% | 32.6% | 96.8% | 48.7% | text-registered (23 anchors) |
| wsdot-XE3771 | XL6985_PS_RS01.dgn | Untitled Sheet | 6/9 | 113 | 93.8% | 60.2% | 84.7% | 98.4% | 91.0% | text-registered (65 anchors) |
| wsdot-XE3771 | XL6985_PS_TC.dgn | Default | 8/9 | 263 | 92.0% | 78.8% | 84.4% | 90.5% | 87.4% | text-registered (139 anchors) |
| wsdot-XE3771 | XL6985_PS_WZTC_TC01.dgn | SHEET_1 | 8/9 | 263 | 95.1% | 79.1% | 90.2% | 96.6% | 93.3% | text-registered (84 anchors) |
| wsdot-XE3771 | XL6985_PS_WZTC_TC02.dgn | SHEET_1 | 8/9 | 263 | 94.7% | 78.8% | 16.9% | 55.3% | 25.9% |  |
