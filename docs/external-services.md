# External services and privacy (page "Standorte")

BewerbungsPilot is a **local, single-user application without login**. Run it only on your own
computer (`localhost`). Everything except the services listed below stays on this computer.

## Services used

| Service | Used for | When | What is sent | Terms |
|---|---|---|---|---|
| OpenStreetMap tile servers (`tile.openstreetmap.org`) | Map background images | Whenever the map is shown | IP address, browser info, visible map area (tile coordinates) – **no application data** | [Tile Usage Policy](https://operations.osmfoundation.org/policies/tiles/), attribution "© OpenStreetMap contributors" shown on the map |
| Nominatim (`nominatim.openstreetmap.org`), via the local server | Turning a place name / address into coordinates | **Only** when the user clicks "Suchen" in the location dialog | The typed search text; User-Agent `BewerbungsPilot/1.0 (local job application manager)` | [Nominatim Usage Policy](https://operations.osmfoundation.org/policies/nominatim/): max. 1 request/s (enforced in `NominatimGeocoder.cs`), results cached, no automatic or bulk geocoding, no search-as-you-type, attribution shown with results |
| Google Maps (`www.google.com/maps/dir/`) | Route planning (public transport, car, bike, walking) | **Only** when the user clicks a route link (opens a new tab) | Destination coordinates or place text, and the starting point if one is set | Documented [Maps URLs](https://developers.google.com/maps/documentation/urls/get-started) (`api=1`), no API key |

No API keys, no paid services, no accounts.

## Data handling

- **Confirmed employer positions** (coordinates, precision, label) are stored in the local SQLite
  database (`storage/`, not in Git). They are only saved after the user confirms them.
- **Starting point** (e.g. home town or a city considered for relocation) is stored only in the
  browser (`localStorage`, key `bewerbungspilot.origin`). To calculate distances it is sent to the
  local server in the request **body** of `POST /api/map/query` – never in a URL – and is neither
  stored nor logged there.
- **Logging:** `System.Net.Http.HttpClient` is set to `Warning` in `appsettings.json`, so geocoding
  URLs (which contain the search text) are not written to the log.
- **Map endpoints** return no notes, documents, contacts or job advertisement links.
- **Distances** are straight-line distances ("Luftlinie", Haversine formula). BewerbungsPilot does not
  calculate driving distances or travel times.

## Configuration

`appsettings.json`, section `Geocoding`:

- `BaseUrl` – Nominatim endpoint (tests point this to a local fake server).
- `UserAgent` – identifies the application to Nominatim, as required by its usage policy.

If geocoding is unavailable (offline, service down), coordinates can be entered by hand in the
location dialog. If map tiles cannot be loaded, the list, distances and route links keep working.

## Before any public deployment

The document and map endpoints have no access control. A public demo must use fictional data only
and must not contain real addresses or starting points.
