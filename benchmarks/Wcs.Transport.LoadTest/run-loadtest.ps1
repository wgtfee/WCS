$ErrorActionPreference = 'Stop'
dotnet restore "$PSScriptRoot/Wcs.Transport.LoadTest.csproj"
dotnet run --project "$PSScriptRoot/Wcs.Transport.LoadTest.csproj" --configuration Release
