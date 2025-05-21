# Debugging Guide

The project is configured with several debug options:

## Debug Console App with Functions
This configuration automatically:
1. Builds the Function App project
2. Starts the Function App in the background
3. Builds and runs the Console App

Use this option when you want to test both applications together.

## Debug Console App Only
Use this configuration when you only want to debug the console application.

## Debug Function App Only
Use this configuration when you only want to debug the Function App.

## Debug Both Apps
This is a compound configuration that runs both the Function App and Console App in separate debug sessions.

## Troubleshooting

If the Function App doesn't start:
1. Make sure you have Azure Functions Core Tools installed
2. Try running the Function App manually: `cd RemoteSync && dotnet run`
3. Check that the required port (7071) is not in use
