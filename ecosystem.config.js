module.exports = {
  apps: [
    {
      name: "nezumi-radio-core",
      script: "./bin/Release/net10.0/NezumiRadio",
      autorestart: true,
      watch: false,
      env: {
        NODE_ENV: "production"
      }
    }
  ],
};
