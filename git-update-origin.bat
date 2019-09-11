@echo off

REM From: https://help.github.com/en/articles/syncing-a-fork

git fetch upstream
git checkout master
git merge upstream/master
