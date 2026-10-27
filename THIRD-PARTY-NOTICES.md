# Third-party notices

AirPDF is built on the shoulders of open-source projects. This file records the
components that are distributed with, or required by, the application.

## Runtime components

| Component | License | Project |
| --- | --- | --- |
| Avalonia | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Fonts.Inter | MIT | https://github.com/AvaloniaUI/Avalonia |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| PDFtoImage | MIT | https://github.com/sungaila/PDFtoImage |
| PdfPig | Apache-2.0 | https://github.com/UglyToad/PdfPig |
| PDFium | BSD-3-Clause | https://pdfium.googlesource.com/pdfium/ |
| SkiaSharp | MIT | https://github.com/mono/SkiaSharp |
| Inter typeface | SIL Open Font License 1.1 | https://rsms.me/inter/ |
| Lucide icons (adapted line geometry) | ISC | https://lucide.dev |

## MIT license

The MIT License applies to Avalonia, Avalonia.Fonts.Inter, CommunityToolkit.Mvvm,
PDFtoImage, and SkiaSharp. Their copyright notices are held by their respective
authors and contributors.

```
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
```

## PDFium

PDFium is licensed under the BSD 3-Clause license. The PDFium copyright notice
and license text are reproduced below.

```
Copyright 2014 The PDFium Authors. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

   * Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.
   * Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.
   * Neither the name of Google Inc. nor the names of its
contributors may be used to endorse or promote products derived from
this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Apache License 2.0 (PdfPig)

PdfPig is licensed under the Apache License, Version 2.0. The full license is
available at https://www.apache.org/licenses/LICENSE-2.0. PdfPig's NOTICE and
attribution information is available in its upstream repository.

## SIL Open Font License 1.1 (Inter)

The Inter typeface is distributed under the SIL Open Font License, Version 1.1.
The full license is available at https://openfontlicense.org/.

## ISC license (Lucide)

Some line-icon geometry in `src/AirPdf.App/Styles/Icons.axaml` is adapted from
Lucide under the ISC license.

```
Copyright (c) for portions of Lucide are held by Cole Bemis 2013-2022 as part
of Feather (MIT). All other copyright (c) for Lucide are held by Lucide
Contributors 2022-2025.

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH
REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY
AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT,
INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM
LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR
OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR
PERFORMANCE OF THIS SOFTWARE.
```

## Sample document

`tests/Assets/sample.pdf` is generated from `tests/Assets/sample.html`, both
created for this repository, and are distributed under the MIT license. Its
typography uses fonts available on the machine that generated it; the PDF
embeds the glyphs needed to display it.
