module.exports = {
  apps: [
    {
      name: "nezumi-radio-core",
      script: "dotnet",
      args: "./bin/Release/net10.0/NezumiRadio.dll",
      autorestart: true,
      watch: false,
      env: {
        NODE_ENV: "production"
      }
    }
  ],
};
