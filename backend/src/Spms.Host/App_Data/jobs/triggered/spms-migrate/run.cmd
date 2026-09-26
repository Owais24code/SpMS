@echo off
rem Azure App Service WebJob (Windows): Spms.Host --migrate with the app's own settings.
rem Run from the portal: the API app -> Settings -> WebJobs -> spms-migrate -> Run.
rem The job folder is copied to a temp directory before it runs, so start from wwwroot
rem (appsettings.json and the published assemblies are there).
cd /d "%HOME%\site\wwwroot"
dotnet Spms.Host.dll --migrate
exit /b %ERRORLEVEL%
