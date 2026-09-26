@echo off
rem Azure App Service WebJob (Windows): Spms.Host --fga-sync with the app's own settings.
rem Run from the portal: the API app -> Settings -> WebJobs -> spms-fga-sync -> Run.
rem The job folder is copied to a temp directory before it runs, so start from wwwroot
rem (appsettings.json and the published assemblies are there).
rem The one place the store is created and a model written; the API itself only attaches.
set Authorization__OpenFga__Bootstrap=true
cd /d "%HOME%\site\wwwroot"
dotnet Spms.Host.dll --fga-sync
exit /b %ERRORLEVEL%
