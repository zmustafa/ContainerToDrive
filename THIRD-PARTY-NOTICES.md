# Third-party notices

The [LICENSE](LICENSE) applies to original ContainerToDrive code, not automatically to dependencies. This inventory is not a completed transitive-license review or an SPDX/CycloneDX SBOM. Developer packages must not be represented as reviewed customer releases.

## rclone 1.75.1

Unmodified upstream engine: [source at v1.75.1](https://github.com/rclone/rclone/tree/v1.75.1), [upstream COPYING](https://raw.githubusercontent.com/rclone/rclone/v1.75.1/COPYING). The full MIT notice below is also retained verbatim in the package's rclone license material. rclone's Go dependencies can impose additional notice obligations; their complete inventory remains a release gate.

Copyright (C) 2012 by Nick Craig-Wood http://www.craig-wood.com/nick/

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.

## WinFsp 2.1 / 2.1.25156

**WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos.** [WinFsp repository](https://github.com/winfsp/winfsp), [pinned v2.1 source](https://github.com/winfsp/winfsp/tree/v2.1), [full upstream license and FLOSS exception](https://raw.githubusercontent.com/winfsp/winfsp/v2.1/License.txt).

WinFsp is GPLv3 with an upstream FLOSS exception, not MIT. The following is a summary, not a replacement for its license: the exception grants qualifying FLOSS applications permission to link to the specified platform WinFsp DLLs and to distribute **unmodified upstream binary installers**, subject to its conditions. These include qualifying FLOSS licensing, the prescribed notice and repository link in the UI and user-facing documentation, and no combination with proprietary software under that exception. Modified WinFsp or a proprietary distribution requires separate review or appropriate commercial licensing.

The application package does **not** embed, own, repair, or uninstall shared WinFsp. Bootstrap can download its separate, unmodified official MSI only on explicit request. Bootstrap also obtains the **full pinned license including GPLv3 and the exception**, validates its reviewed SHA-256, and packaging includes those exact bytes. Missing/unreviewed license material blocks packaging. Preserve upstream signatures; never re-sign the WinFsp MSI as a ContainerToDrive artifact. UI attribution is the source owner's responsibility and remains a release gate.

## OxyPlot 2.2.0

The desktop charts use OxyPlot.Core, OxyPlot.Wpf, and OxyPlot.Wpf.Shared 2.2.0. [Pinned upstream source](https://github.com/oxyplot/oxyplot/tree/v2.2.0), [upstream MIT license](https://github.com/oxyplot/oxyplot/blob/v2.2.0/LICENSE).

MIT License

Copyright (c) 2014 OxyPlot contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## .NET and test/build tooling

Self-contained application packages contain Microsoft .NET and Windows Desktop runtime components. Packaging copies full runtime license and third-party notice material from the actual restored runtime packs, and records versions from publish metadata. These notices do not imply every runtime component is MIT.

The development-only test project pins Microsoft.NET.Test.Sdk 17.14.1, xunit 2.9.3, and xunit.runner.visualstudio 3.1.1. Test tooling is not intentionally included in customer payloads. Review exact package licenses and transitive dependencies before distributing any tooling.

WiX 6.0.2 is a **candidate build-tool pin**, not a statement that its availability, maintenance/usage terms, or generated MSI behavior have been validated. The repository does not install WiX automatically. Review [upstream WiX](https://github.com/wixtoolset/wix/tree/v6.0.2) before approving that toolchain.

The generated dependency inventory reports its incomplete review status honestly. Complete transitive notices, source availability obligations, and third-party redistribution review are required before distributing binary releases.