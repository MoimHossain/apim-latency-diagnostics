#!/bin/bash

echo "Starting 5-minute load test..."
echo "Start time: $(date)"

# Calculate end time (5 minutes from now)
END_TIME=$(date -d "5 minutes" +%s)
COUNTER=0
SUCCESS=0
FAILED=0

# Function to handle cleanup on Ctrl+C
cleanup() {
    echo -e "\n\nLoad test interrupted!"
    echo "Total requests sent: $COUNTER"
    echo "Successful: $SUCCESS"
    echo "Failed: $FAILED"
    exit 0
}

trap cleanup SIGINT

# Main load test loop
while [ $(date +%s) -lt $END_TIME ]; do
    COUNTER=$((COUNTER + 1))
    
    # Run curl in background to maintain concurrency
    (
        RESPONSE=$(curl -s -w "%{http_code}" -X POST \
            -H 'accept: application/json' \
            -H 'Content-Type: application/json' \
            -d @postdata.json \
            https://signin-gjeecgcwgkc7bbau.westeurope-01.azurewebsites.net/transaction \
            -o /dev/null 2>/dev/null)
        
        if [[ "$RESPONSE" =~ ^2[0-9][0-9]$ ]]; then
            echo "Request $COUNTER: SUCCESS ($RESPONSE)"
        else
            echo "Request $COUNTER: FAILED ($RESPONSE)"
        fi
    ) &
    
    # Control concurrency - wait if we have too many background jobs
    if (( COUNTER % 10 == 0 )); then
        wait
        echo "Completed $COUNTER requests..."
    fi
    
    # Small delay to prevent overwhelming the server
    sleep 0.1
done

# Wait for any remaining background jobs
wait

echo -e "\nLoad test completed!"
echo "End time: $(date)"
echo "Total requests sent: $COUNTER"
echo "Duration: 5 minutes"
