@echo off

REM From: https://help.github.com/en/articles/syncing-a-fork

git fetch upstream
git checkout master
git merge upstream/master

REM git fetch upstream
REM git checkout v2.1-dev
REM git merge upstream/v2.1-dev
