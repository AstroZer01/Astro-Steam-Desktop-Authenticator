# Contributing to Astro Steam Desktop Authenticator

First off, thank you for considering contributing to Astro Steam Desktop Authenticator! It's people like you that make Astro Steam Desktop Authenticator such a great tool for the community.

## Where do I go from here?

If you've noticed a bug or have a feature request, make sure to check our [Issues](../../issues) to see if someone else has already created a ticket. If not, go ahead and make one!

## Fork & create a branch

If this is something you think you can fix, then fork Astro Steam Desktop Authenticator and create a branch with a descriptive name.

A good branch name would be (where issue #325 is the ticket you're working on):

`sh
git checkout -b 325-fix-qr-login-timeout
`

## Get the test suite running

Make sure you're using the **.NET 8 SDK** and that the project builds locally on your machine. You can compile the project by running:
`ash
dotnet build "Launcher/Launcher.csproj"
`

## Implement your fix or feature

At this point, you're ready to make your changes. Feel free to ask for help; everyone is a beginner at first.

## Make a Pull Request

At this point, you should switch back to your master branch and make sure it's up to date with Astro Steam Desktop Authenticator's master branch:

`sh
git remote add upstream https://github.com/AstroZer01/Astro-Steam-Desktop-Authenticator.git
git checkout main
git pull upstream main
`

Then update your feature branch from your local copy of master, and push it!

`sh
git checkout 325-fix-qr-login-timeout
git rebase main
git push --set-upstream origin 325-fix-qr-login-timeout
`

Finally, go to GitHub and make a Pull Request!
