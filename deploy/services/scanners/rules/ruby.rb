# The weak code each rule in ruby.yaml must find (ruleid) and the fixed code it must not (ok).
require "open3"
require "yaml"

class UsersController < ApplicationController
  def sql(name)
    # ruleid: ruby-sql-built-from-strings
    User.where("name = '#{name}'")
    # ruleid: ruby-sql-built-from-strings
    User.find_by_sql("SELECT * FROM users WHERE name = '" + name + "'")
    # ok: ruby-sql-built-from-strings
    User.where("name = ?", name)
    # ok: ruby-sql-built-from-strings
    User.where(name: name)
  end

  def show
    # ruleid: ruby-sql-from-request, ruby-sql-built-from-strings
    @users = User.where("name = '#{params[:name]}'")
    # ok: ruby-sql-from-request
    @others = User.where(name: params[:name])
  end

  def commands(host)
    # ruleid: ruby-command-from-strings
    system("ping -c 1 #{host}")
    # ruleid: ruby-command-from-strings
    `ping -c 1 #{host}`
    # ok: ruby-command-from-strings
    system("ping", "-c", "1", host)
  end

  def ping
    # ruleid: ruby-command-from-request
    Open3.capture2(params[:cmd])
    # ok: ruby-command-from-request
    Open3.capture2("uptime")
  end

  def deserialise(blob)
    # ruleid: ruby-unsafe-deserialization
    Marshal.load(blob)
    # ruleid: ruby-unsafe-deserialization
    YAML.unsafe_load(blob)
    # ok: ruby-unsafe-deserialization
    YAML.safe_load(blob)
    # ok: ruby-unsafe-deserialization
    JSON.parse(blob)
  end

  def code(expr)
    # ruleid: ruby-code-from-data
    eval(expr)
    # ruleid: ruby-code-from-data
    @thing.send(params[:method])
    # ok: ruby-code-from-data
    @thing.send(:name)
  end

  def html(comment)
    # ruleid: ruby-html-not-escaped
    a = "<p>#{comment}</p>".html_safe
    # ruleid: ruby-html-not-escaped
    b = raw(comment)
    # ok: ruby-html-not-escaped
    c = "<br>".html_safe
    # ok: ruby-html-not-escaped
    d = raw(sanitize(comment))
    [a, b, c, d]
  end

  def go
    # ruleid: ruby-redirect-from-request
    redirect_to params[:next]
  end

  def go2
    # ok: ruby-redirect-from-request
    redirect_to root_path
  end

  def create
    # ruleid: ruby-mass-assignment
    User.create(params.permit!)
    # ok: ruby-mass-assignment
    User.create(params.require(:user).permit(:name, :email))
  end

  def tls(http)
    # ruleid: ruby-tls-not-verified
    http.verify_mode = OpenSSL::SSL::VERIFY_NONE
    # ok: ruby-tls-not-verified
    http.verify_mode = OpenSSL::SSL::VERIFY_PEER
  end
end
