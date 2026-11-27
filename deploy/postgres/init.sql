-- Local development only: creates the three roles with known passwords before migrations run.
-- Production: roles and passwords come from the infrastructure stack and a secrets manager.
CREATE ROLE relaystack_owner LOGIN PASSWORD 'owner_dev_password' CREATEROLE;
CREATE ROLE relaystack_app LOGIN PASSWORD 'app_dev_password';
CREATE ROLE relaystack_worker LOGIN PASSWORD 'worker_dev_password';
CREATE DATABASE relaystack OWNER relaystack_owner;
