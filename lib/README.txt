# Microsoft.Web.WebView2 SDK 1.0.4129.50

Extracted from the official NuGet package and vendored here so that build.ps1
works without network access:

    https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4129.50

A .nupkg is a zip archive. To verify these files come from that package
untouched, download it and compare:

    lib/Microsoft.Web.WebView2.Core.dll      <- lib/net462/Microsoft.Web.WebView2.Core.dll
    lib/Microsoft.Web.WebView2.WinForms.dll  <- lib/net462/Microsoft.Web.WebView2.WinForms.dll
    lib/WebView2Loader.x64.dll               <- runtimes/win-x64/native/WebView2Loader.dll
    lib/WebView2Loader.x86.dll               <- runtimes/win-x86/native/WebView2Loader.dll

SHA-256 of the files in this folder:

    Microsoft.Web.WebView2.Core.dll      958EFDB7F13A6D1F3079756C96956CC96CF713AE46FA085C8B1E7F44316A4F7E
    Microsoft.Web.WebView2.WinForms.dll  A7B8BE525030F19D9E88C6E684BCA053DC7A3B080C31C3D9428F7438E7B6768F
    WebView2Loader.x64.dll               A9A09232C25805323D4CFB3FC8F545A190A9C8A99C93262EA99D0B88DF99EC90
    WebView2Loader.x86.dll               CBCD9A820B23AEC9D68A95FB8CFD8C7D48E5BAC1129FAAF87AECABF4409A2EE2

These binaries are redistributed under the terms that ship with the WebView2
SDK. They are Microsoft's, not ours.
