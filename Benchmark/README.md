

# K8S

```
ab -t 10 -c 10 -k -v 4 -q -T 'application/json' -p postdatak8s.json -H 'accept: application/json' https://signin.4.175.191.234.nip.io/transaction
```

# App service (to APIM to APp service)

```
ab -t 300 -c 10 -k -v 4 -q -T 'application/json' -p postdata.json -H 'accept: application/json' https://signin-gjeecgcwgkc7bbau.westeurope-01.azurewebsites.net/transaction
```

# App Service (Direct to App service)

```
ab -t 300 -c 10 -k -v 4 -q -T 'application/json' -p postdata-direct.json -H 'accept: application/json' https://signin-gjeecgcwgkc7bbau.westeurope-01.azurewebsites.net/transaction-direct
```