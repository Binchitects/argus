# v9: IoT and OT, on the company's own hardware

Arena's next ground: the plant, the building and the site. Cameras, sensors and machines, read where they are,
kept on the company's hardware, watched by models, and asked in plain words — as the chat, Code Arena and the
dashboards are today. Read-only first; any control of a machine behind an approval, always.

## What it is for

- **"Ask your plant"**: "why did line 3 stop at 14:20?", "which pumps ran hot this week?", "show me who was in
  bay 2 without a helmet today" — answered from the sensors, the alarms, the cameras and the plant's own manuals.
- **Many cameras, watched by models**: hundreds to thousands of streams, recorded, searched by what happened
  ("a forklift in the walkway"), events raised from what a model sees, not from motion alone.
- **Sensors and machines**: every value of every device in one place, with its unit, its asset and its limits;
  alarms that are managed, not a flood; trends, forecasts and the reports a shift needs.
- **Agents for operations**: an alarm starts an agent (Agent Studio) that gathers the trend, the clip and the
  manual's page, writes the first analysis and opens the work order.

## The parts

### 1. Assets: what is where

- Sites → areas → lines/rooms → assets (a pump, a door, a camera, a PLC) → points (a temperature, a state, a count),
  each point with its unit, range, limits, and where it comes from. Imported from a spreadsheet or discovered
  (OPC UA browse, ONVIF discovery, MQTT topics seen).
- Floor plans and maps with each asset and camera pinned; the dashboards and the chat link to them.
- Permissions per site and area, as groups and projects have them now (who sees which cameras and points).

### 2. Ingestion: reading the devices

- **MQTT** broker in the stack (Mosquitto or EMQX), Sparkplug B understood; **OPC UA** client (subscriptions,
  browse); **Modbus TCP/RTU** (through a gateway), **BACnet/IP** (buildings), **SNMP** (network gear), HTTP and
  webhooks; CSV and historian imports (PI, InfluxDB) for what came before.
- A connector per protocol, each a small service of its own, read-only by default, its credentials kept as the
  stack keeps secrets.
- **Time series**: TimescaleDB in the stack's Postgres (one database to back up, its compression and retention),
  or VictoriaMetrics beside Prometheus for very high rates; a downsampled copy for long ranges.
- **Store and forward** at the edge: nothing is lost when the link to the site drops.

### 3. Cameras

- RTSP and ONVIF streams through a media server (MediaMTX or go2rtc): live view in the browser (WebRTC, HLS), a
  camera wall, PTZ where the camera has it.
- Recording to disk with retention per camera (continuous, on event, or both), clips exported.
- **Video analytics on the GPUs** as the stack shares them now: detection and tracking (YOLO-class models through
  ONNX Runtime or TensorRT, DeepStream for many streams), zones and lines drawn on the frame, people and vehicle
  counts, PPE (helmet, vest), intrusion, loitering, smoke and fire, licence plates where the law allows.
- **Ask the camera**: a vision-language model (Qwen2.5-VL, already served) describes a frame or a clip and answers
  questions about it; frames embedded (CLIP-class) so footage is searched by words.
- Scale: sub-streams for analytics, motion-gated inference, batching across streams, a model per GPU budget;
  thousands of cameras over several machines, as models spread over engines now.
- Privacy: faces and plates blurred in what is shown and exported unless the person may see them, retention and
  access per camera, every view and export audited.

### 4. Alarms and events

- Rules on points and on what cameras see: thresholds with deadbands, rate of change, state changes, a value
  stuck, "no data", combinations; anomaly detection learned per point.
- The alarm lifecycle as ISA-18.2 has it: active, acknowledged, cleared, shelved, suppressed; priorities; flood
  and chattering detection; who acknowledged what and when.
- Notifications as the stack has them (the bell, push, email), escalation and on-call, and an agent started.

### 5. Dashboards and reports

- The dashboards engine with a time-series datasource: live values, trends, gauges, states, floor plans, camera
  tiles; OEE (availability, performance, quality) for lines; energy per site.
- Reports in PDF, Excel and CSV as Accounting's are: shift reports, alarm statistics, downtime, energy.

### 6. AI on top

- Chat tools: `plant_points` (find points), `plant_series` (values over time, aggregated), `plant_alarms`,
  `camera_frame` / `camera_search` / `camera_ask`, `asset_docs` (the asset's manuals and procedures from knowledge
  packs); a model answers with the chart and the clip.
- Root-cause help: what changed before the alarm, across the asset's points and its neighbours.
- Forecasts and predictive maintenance: a point's trend, its time to a limit; models trained here, on the site's
  own history (no data leaves).
- Shift handover and incident reports written by an agent from the events, checked by a person.

### 7. Edge

- An edge gateway (one binary, as Code Arena is): the connectors, a small buffer, local inference on a Jetson or a
  small GPU, sending upstream over one outbound connection; managed from Arena (its config, its version, its health).
- Fleet: many sites, each with its gateways, updated from Arena (offline bundles as the stack's own).

### 8. OT safety and security

- Read-only by default. Writing a setpoint or a command: off unless an admin turns it on for a point, then behind an
  approval (and two people for what is marked critical), with limits, and audited; never from a model alone.
- The Purdue model respected: connectors in the OT network or a DMZ, data diodes supported (one way out only),
  no inbound path from IT to OT.
- IEC 62443 controls in the compliance packs beside ISO 27001 and SOC 2 (zones and conduits, accounts, logging,
  patching), the evidence gathered as the stack gathers it now.

## Releases

Each with something to show (docs/plan.md's rule):

| Release | Headline |
|---|---|
| **9.0 Foundation** | Assets and floor plans; MQTT and OPC UA and Modbus read; time series in TimescaleDB; live dashboards; cameras live and recorded (MediaMTX); alarms with the lifecycle; "ask your plant" over points and alarms |
| **9.1 Vision** | Detection, zones and counts on the GPUs; PPE and intrusion events; search footage by words; ask the camera (VLM); privacy blurring |
| **9.2 Edge and fleet** | The edge gateway, store and forward, local inference, many sites managed from Arena |
| **9.3 Operations AI** | Anomaly detection, forecasts and predictive maintenance; alarm-started agents; root-cause help; shift reports; CMMS work orders |
| **9.4 Control and compliance** | Setpoints behind approvals and two-person rule; IEC 62443 pack; data diode mode |

## What it needs

- Images and models to download (each asked for with its size before): Mosquitto or EMQX, MediaMTX, TimescaleDB
  (Postgres's extension), an OPC UA and a Modbus library, YOLO-class detection models, a CLIP-class embedder.
- Test rigs without real plants: an OPC UA simulator, a Modbus simulator, MQTT publishers replaying recorded data,
  RTSP streams from video files; load tests for thousands of points a second and hundreds of streams.
- A camera or two and a sensor kit for the real checks, when the user has them.

## Built so far (branch v9.0)

- **Assets**: sites (seen by their groups), areas one under another, assets and points with units, sources and
  limits; a sheet of points imported whole (`POST /api/plant/sites/{id}/import`). Workspace → Plant shows them.
- **Values over HTTP**: a connector or a gateway sends values with its person's API key, which needs `plant.write`
  (and the sites it sees). A batch of up to 10,000, each to a point by id or by its source and address:

  ```bash
  curl -X POST https://arena.example/api/plant/samples \
    -H "Authorization: Bearer $ARENA_KEY" -H "X-Requested-With: gateway" -H "Content-Type: application/json" \
    -d '{"samples":[{"source":"opcua","address":"ns=2;s=P101.Temp","value":71.5},{"address":"site/hall1/door2/open","on":true}]}'
  ```

  The key is made in Connect your tools (never put one in a script that is shared). A value dated more than five
  minutes ahead is refused; the same point and moment sent again replaces it. The answer says how many were kept,
  and which were for no point the key sees or wrong for their point's type.
- **Reading them**: `GET /api/plant/points/{id}/series?from=&to=&buckets=` (average, lowest, highest, last and how
  many a bucket; a text point's texts), and each point's latest value with its asset. Stored in Postgres
  (`plant_samples`, a row a point and moment) until TimescaleDB is in the stack.

## Open questions for the user

- Which protocols first (what the sites run: Siemens S7 via OPC UA, Modbus, BACnet, MQTT)?
- How many cameras and points at the first site, and which GPUs for video?
- Which analytics matter most first (safety/PPE, intrusion, counting, quality)?
- Is any control of machines wanted at all, or read-only for good?
