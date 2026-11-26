-- Two separate databases: the app's own, and the "existing" legacy Orders system.
CREATE DATABASE brightline_legacy;
\connect brightline_app
CREATE EXTENSION IF NOT EXISTS vector;
