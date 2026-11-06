# Third-party notices

This file covers third-party material used by the SRdeck host and public plugin platform. Plugin-specific notices are distributed with the applicable plugin. Exact upstream license and notice files available from NuGet packages are also included under `licenses/` in official binary ZIP packages.

## MIT-licensed components

- CommunityToolkit.Mvvm 8.4.0 — Copyright .NET Foundation and Contributors
- Microsoft.Extensions.DependencyInjection 9.0.0 — Copyright .NET Foundation and Contributors
- Microsoft.Extensions.DependencyInjection.Abstractions 9.0.0 — Copyright .NET Foundation and Contributors
- Microsoft.Xaml.Behaviors.Wpf 1.1.142 — Copyright Microsoft Corporation
- System.IO.Ports 10.0.0 — Copyright .NET Foundation and Contributors

Licensed under the MIT License:

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the “Software”), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NON-INFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Leaflet 1.9.4

Leaflet's version-pinned JavaScript and CSS distribution files are embedded in `SRdeckPlugin.Wpf`, so the map renderer itself does not require a CDN connection. The vendored files match the SHA-256 hashes published for Leaflet 1.9.4. Source: <https://github.com/Leaflet/Leaflet/tree/v1.9.4>.

BSD 2-Clause License

Copyright (c) 2010-2023, Volodymyr Agafonkin
Copyright (c) 2010-2011, CloudMade
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## OpenStreetMap data and standard tiles

Map data is copyright OpenStreetMap contributors and is available under the Open Database License (ODbL) 1.0. The map displays a persistent link to <https://www.openstreetmap.org/copyright>.

Connected map views request only the raster tiles needed for the current viewport from `https://tile.openstreetmap.org/{z}/{x}/{y}.png`. Requests use an SRdeck-identifying User-Agent. Viewed tiles are cached in a bounded local SQLite database and are revalidated with `ETag` or `Last-Modified` after the server-provided freshness lifetime. When offline, cached tiles remain available after that lifetime. SRdeck does not bulk-download, prefetch, create, or redistribute OpenStreetMap standard tiles. The community tile service is best-effort and is governed separately from the data license; see the current [OpenStreetMap tile usage policy](https://operations.osmfoundation.org/policies/tiles/) and OSMF terms before changing this integration.

## rtl-sdr API declarations

The managed declarations in `SRdeck/SDR/RtlSdrApi.cs` correspond to the public rtl-sdr API from Osmocom. The upstream `rtl-sdr.h` is GPL-2.0-or-later and bears these notices:

- Copyright (C) 2012-2013 Steve Markgraf
- Copyright (C) 2012 Dimitri Stolnikov

SRdeck does not distribute `rtlsdr.dll`. Users obtain a compatible native library separately. The SRdeck source and combined work are distributed under GPL-3.0-only; the complete GPL-3.0 license appears in `LICENSE`. Upstream source: <https://gitea.osmocom.org/sdr/rtl-sdr>.

## libhackrf API declarations

The managed declarations in `SRdeck/SDR/HackRfApi.cs` correspond to the public
libhackrf API from Great Scott Gadgets. SRdeck does not distribute `hackrf.dll`,
`libusb-1.0.dll`, or the HackRF Tools package. Users obtain compatible 64-bit
native libraries separately and must comply with their accompanying licenses.
Upstream source: <https://github.com/greatscottgadgets/hackrf>.

## Microsoft.Data.Sqlite 10.0.12

Microsoft.Data.Sqlite and its SQLitePCLRaw dependencies maintain the bounded map-tile cache. Microsoft.Data.Sqlite and SQLitePCLRaw are distributed under the MIT License. The bundled SQLite library is in the public domain.

## SDRplay API declarations

`SRdeck/SDR/SdrPlayApi.cs` contains managed interoperability declarations for SDRplay API 3.15. SRdeck does not distribute `sdrplay_api.dll` or SDRplay's proprietary API package. Obtain the API from SDRplay and comply with its current terms, including its restriction to genuine SDRplay products. Official downloads and terms: <https://www.sdrplay.com/api/>.

## Microsoft.Web.WebView2 1.0.4078.44

Copyright (C) Microsoft Corporation. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.
* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.
* The name of Microsoft Corporation, or the names of its contributors may not
  be used to endorse or promote products derived from this software without
  specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
