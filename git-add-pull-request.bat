@echo off

REM Idea from: https://stackoverflow.com/questions/25955822/git-cherry-pick-a-single-commit-for-pull-request

set BRANCH_NAME=%1
set COMMIT_HASH=%2

IF %BRANCH_NAME%.==. GOTO NoBranchName
IF %COMMIT_HASH%.==. GOTO NoCommitId

git checkout -b %BRANCH_NAME%
git fetch upstream
git reset --hard upstream/master
git cherry-pick %COMMIT_HASH%
git push origin %BRANCH_NAME%:%BRANCH_NAME%
git switch master

GOTO End

:NoBranchName
  ECHO No branch name given. Usage: add-pull-request.bat BRANCH_NAME COMMIT_HASH
GOTO End

:NoCommitId
  ECHO No commit id given. Usage: add-pull-request.bat BRANCH_NAME COMMIT_HASH
GOTO End

:End
