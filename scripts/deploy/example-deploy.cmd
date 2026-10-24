@echo off
rem Sample Windows deploy target (no PowerShell required).
rem Replace the echo calls with the real dev/UAT command.
setlocal
if "%ANIMA_DEPLOY_REPO%"=="" echo ANIMA_DEPLOY_REPO is required & exit /b 1
if "%ANIMA_DEPLOY_REF%"=="" echo ANIMA_DEPLOY_REF is required & exit /b 1

echo deploying %ANIMA_DEPLOY_REPO%@%ANIMA_DEPLOY_REF% to %ANIMA_DEPLOY_ENVIRONMENT%
echo commit %ANIMA_DEPLOY_BEFORE% -^> %ANIMA_DEPLOY_AFTER% by %ANIMA_DEPLOY_PUSHER%
echo rule %ANIMA_DEPLOY_RULE% (run %ANIMA_DEPLOY_RUN_ID%)

rem Example: call the real deploy step.
rem some-deploy-tool.exe --env %ANIMA_DEPLOY_ENVIRONMENT% --sha %ANIMA_DEPLOY_AFTER%

exit /b 0
